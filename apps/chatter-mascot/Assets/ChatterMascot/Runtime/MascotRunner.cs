using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatterMascot.Audio;
using ChatterMascot.Net;
using ChatterMascot.Playback;
using ChatterMascot.Protocol;
using UnityEngine;

namespace ChatterMascot
{
    /// <summary>
    /// 配信された発話を音にする常駐コンポーネント。<c>core/src/player/index.ts</c> にあたる。
    ///
    /// <b>判断ロジックを持たない。</b> 何をいつ取りに行き、いつ鳴らし、いつ ack するかは
    /// <see cref="PlaybackQueue"/> に出してある。ここはコマンドを実行して結果をイベントとして
    /// 戻すだけのドライバと、起動・終了の配線。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MascotRunner : MonoBehaviour
    {
        [Header("接続先")]
        [Tooltip("chatter-agent-server の WebSocket。音声は同じ authority から HTTP で取る")]
        [SerializeField] private string serverUrl = "ws://127.0.0.1:8570";

        /// <summary>
        /// フレームレートの上限。
        ///
        /// ★ <b>Unity の既定は無制限。</b> テンプレートの <c>vSyncCount: 0</c> と
        ///   <c>Application.targetFrameRate = -1</c> の組み合わせで、Cube 1個のシーンでも
        ///   <b>CPU 261% / GPU 93.5%</b> まで行った（実測）。常駐アプリなので電力に直接効く。
        ///
        /// ★ <b><c>vSyncCount</c> ではなくこちらで絞ること。</b>
        ///   <c>targetFrameRate</c> は VSync が有効だと<b>無視される</b>ので、
        ///   <c>vSyncCount: 0</c> のままの方が確実に効く（透過ウィンドウで VSync が効くかも不明）。
        ///
        /// ★ <b>この 30 はデスクトップ限定の値。</b> Android XR ではヘッドセットの
        ///   リフレッシュレートに合わせる必要がある（→ #25）。VRM のリップシンクと
        ///   spring bone が入ったら見直す（→ #17）。
        /// </summary>
        [Header("表示")]
        [Tooltip("フレームレートの上限。常駐アプリなので電力に直接効く。0 以下なら制限しない")]
        [SerializeField] private int targetFrameRate = 30;

        [Header("再生")]
        [Tooltip("音を出す AudioSource。未設定なら自分に付いているものを使う")]
        [SerializeField] private AudioSource audioSource;

        /// <summary>
        /// 無音がこれだけ続いたらオーディオ出力デバイスを手放す。
        ///
        /// ★ <b>効き方はプラットフォームで違う</b>（→ <c>SpeechPlayerFactory</c>）。
        ///   <b>macOS では何もしない</b> —— 1発話 = 1プロセスなので、鳴り終われば
        ///   OS がデバイスを解放する。手放すものが残っていない。
        ///   <b>Android / iOS でだけ</b> <c>AudioSettings.Mobile.StopAudioOutput()</c> が走る。
        ///
        /// ★ <b>短くしすぎないこと。</b> 文と文の間で往復すると、Bluetooth では
        ///   A2DP の張り直しが毎文入って<b>かえって悪化する</b>。長すぎる害は
        ///   省電力が薄れるだけ（無害側）。
        /// </summary>
        [Tooltip("無音がこれだけ続いたら出力デバイスを手放す（ミリ秒）。0 以下で無効")]
        [SerializeField] private int audioIdleSuspendMs = 5000;

        [Header("キュー")]
        [Tooltip("再生中の1件を含めて、いくつ先まで音声を取りに行くか")]
        [SerializeField] private int lookahead = 3;

        [Tooltip("これより古い発話は音を出さずに飛ばす（ミリ秒）。0 なら無効")]
        [SerializeField] private int speechMaxAgeMs = 0;

        [Tooltip("音声の取得1回あたりの上限（ミリ秒）。サーバーの合成タイムアウトと揃える必要は無い")]
        [SerializeField] private int audioFetchTimeoutMs = 60000;

        /// <summary>
        /// 時間で進む判断（古さ・stall watchdog・503 のバックオフ）を進める間隔。
        ///
        /// ★ <c>PlaybackOptions.AudioRetryMs</c>（既定1秒）と揃えること。503 からの取り直しは
        ///   「バックオフが明けた後の最初の Tick」で起きるので、ここが長いとその分だけ復帰が遅れる。
        /// </summary>
        private const float TickIntervalSeconds = 1f;

        private PlaybackState _state;

        /// <summary>
        /// #59 の <see cref="ChatterMascot.Playback.SpeakingView"/> が読む。<b>読むだけ</b>。
        /// まだ <c>Start</c> 前なら <c>null</c>。
        ///
        /// ★ <b>書き込むと <see cref="PlaybackQueue"/> の状態機械が壊れる。</b>
        ///   コマンドを増やす口ではない。
        /// ★ <b>#58 が <c>SpeakingSet</c> を入れたら消せる。</b>
        /// </summary>
        public PlaybackState State => _state;

        private SpeechClient _client;
        private AudioFetcher _fetcher;
        private ISpeechPlayer _player;
        private AudioIdleGate _idleGate;

        /// <summary>
        /// 取得済みの音声。キーは <c>"{epoch}:{seq}"</c>。
        ///
        /// ★ <b>中身は再生の実体ごとに違う</b>（<c>ISpeechPlayer.Prepare</c> が作る不透明なハンドル）。
        ///   解放は <c>_player.Discard</c> に任せ、ここでは寿命だけを見る。
        /// ★ <b><c>seq</c> だけをキーにしないこと。</b> 採番のやり直しを跨いだ瞬間に別の文と衝突する。
        /// ★ <b>サーバーの epoch をそのままキーに使わないこと</b>（外部由来の文字列）。
        ///   状態機械が読み替えた<b>プロセス内の連番</b>を使う。
        /// </summary>
        private readonly Dictionary<string, object> _handles = new Dictionary<string, object>();

        /// <summary>
        /// 終了を待たせるのに使える予算。
        ///
        /// ★ 切ること。応答しない相手を掴むと<b>アプリが終了しなくなる</b>。
        ///   参照実装（<c>core/src/player/index.ts</c>）の <c>step()</c> も予算付きで待っている。
        /// </summary>
        private const int ShutdownBudgetMs = 3000;

        private float _nextTickAt;
        private bool _shuttingDown;
        private bool _quitRequested;

        /// <summary>
        /// 接続ごとに1回で足りる警告のラッチ。
        ///
        /// ★ 読めないフレームは<b>壊れたプロデューサーが同じ形を送り続ける</b>ので、
        ///   毎フレーム出すとログが洪水になる。
        /// </summary>
        private bool _warnedBadFrame;
        private bool _audioDeclarationChecked;

        private void Awake()
        {
            // ★ 何より先に上限を入れる。無制限のままだと Update / コルーチンが毎秒数千回回り、
            //   メインスレッドが飽和して**サーバーの ping に pong を返せなくなる**
            //   （症状は「アプリが重い」より先に「接続が繰り返し切れる」として出る）
            // ★ **Application.targetFrameRate を直接書かないこと。** VRM の読み込み中だけ
            //   上限を上げる借り手が居る（VrmStage）ので、「戻す先」の宣言をここ1箇所に集める。
            //   直接書くと、借り手が Awake より先に走ったときに Unity 既定の -1 を
            //   保存して復元し、**上限が恒久的に消える**（→ FrameRateBudget）
            FrameRateBudget.SetBaseline(targetFrameRate);

            if (audioSource == null) audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;

            Application.wantsToQuit += OnWantsToQuit;
        }

        /// <summary>
        /// 終了を1回だけ保留して、ack を投げ切ってから閉じる。
        ///
        /// ★ <b><see cref="OnDestroy"/> では間に合わない。</b> そこから
        ///   <c>_ = client.CloseAsync()</c> を投げても、await の継続が走る前に
        ///   プロセスが消える。喋り終えた ack が落ちると、次回起動でその文がもう一度鳴る。
        ///
        /// ★ <b>Editor の Play Mode 停止では保留できない。</b> Unity のドキュメントが
        ///   「The return value of this event is ignored when exiting Play mode in the Editor」と
        ///   明記している。イベント自体は呼ばれるが <c>false</c> が効かないので、
        ///   <b>この経路の確認はビルドした <c>.app</c> で行うこと</b>。
        ///
        /// ★ 保留できない経路（強制終了 / <c>SIGKILL</c>）では ack が落ちるが、
        ///   次回起動でその文がもう一度鳴るだけ。<b>取りこぼしより二重発話の方が軽い。</b>
        /// </summary>
        private bool OnWantsToQuit()
        {
            if (_quitRequested) return true;
            _quitRequested = true;
            _ = ShutdownThenQuitAsync();
            return false;
        }

        private async Task ShutdownThenQuitAsync()
        {
            _shuttingDown = true;

            var client = _client;
            _client = null;
            if (client != null)
            {
                try
                {
                    // 予算内で閉じ切る。返らない相手のためにアプリを終了させないことはしない
                    await Task.WhenAny(client.CloseAsync(), Task.Delay(ShutdownBudgetMs));
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Mascot] 終了処理で例外が出ました: " + e.Message);
                }
            }

            Application.Quit();
        }

        private void Start()
        {
            // ★ **ここで検査しないと「動いて見える死体」になる。**
            //   下の DeriveAudioBaseUrl は new Uri() を呼ぶので、Inspector に
            //   `127.0.0.1:8570`（スキーム無し）や空文字を入れただけで UriFormatException が
            //   飛び、Start() が最後まで走らない。すると _client が null のまま
            //   Update() の Tick も Dispatch も何も起こさず、**ウィンドウは出て、
            //   フレームレート上限も効いて、接続先のログすら出ない**。
            //   Player.log に埋もれたスタックトレース1本以外に手がかりが残らない
            // ★ 測定や切り分けのためにコマンドラインから差し替えられる。繋がらない URL を渡せば
            //   「サーバーに一度も繋がない状態」を**本番シーンのまま**作れる（→ docs/mascot.md）。
            //   専用のシーンを複製すると、#17 で VRM が入った瞬間に本番を代表しなくなり、
            //   **しかも失敗が見えない**（変わらずビルドでき、変わらず計測でき、
            //   ただ別のアプリを測っているだけになる）。
            //
            //   open Build/ChatterMascot.app --args -serverUrl ws://127.0.0.1:9
            var overridden = CommandLine.Argument("-serverUrl");
            if (!string.IsNullOrEmpty(overridden))
            {
                Debug.Log($"[Mascot] serverUrl をコマンドラインで上書きします: \"{overridden}\"");
                serverUrl = overridden;
            }

            if (!IsValidServerUrl(serverUrl))
            {
                Debug.LogError($"[Mascot] serverUrl が不正です: \"{serverUrl}\"。" +
                               "ws:// か wss:// で始まる絶対 URL を指定してください（例: ws://127.0.0.1:8570）");
                enabled = false;
                return;
            }

            var options = new PlaybackOptions
            {
                Lookahead = lookahead,
                MaxAgeMs = speechMaxAgeMs,
                // サーバーは自分のキューにある分しか再送できないので、その上限を覚えていれば足りる
                SeenCapacity = 512,
            };
            _state = new PlaybackState(options);

            _player = SpeechPlayerFactory.Create(audioSource);
            _player.Warn += message => Debug.LogWarning("[Mascot] " + message);
            _idleGate = new AudioIdleGate(audioIdleSuspendMs)
            {
                // ★ **手放せない実装ではゲートごと止める。** 呼んでも何も起きない実装で回すと、
                //   何も手放していないのに「手放しました」とログに出続け、次のデバッグを誤誘導する
                Enabled = audioIdleSuspendMs > 0 && _player.CanSuspendOutput,
            };
            if (!_idleGate.Enabled)
            {
                Debug.Log(audioIdleSuspendMs > 0
                    ? "[Mascot] このプラットフォームでは出力デバイスを手放せないので、アイドル判定は動かしません"
                    : "[Mascot] audioIdleSuspendMs が 0 以下なのでアイドル判定は動かしません");
            }
            // 音声は WebSocket と同じ authority から取る。サーバーは自分の到達アドレスを
            // 知らないので、フレームには相対パスしか載らない
            _fetcher = new AudioFetcher(AudioFetcher.DeriveAudioBaseUrl(serverUrl), audioFetchTimeoutMs);

            _client = new SpeechClient(serverUrl);
            _client.FrameReceived += OnFrame;
            _client.Connected += OnConnected;
            _client.Disconnected += () => Dispatch(PlaybackEvent.Disconnected());
            _client.Log += message => Debug.Log("[Mascot] " + message);
            _client.Warn += message => Debug.LogWarning("[Mascot] " + message);

            Debug.Log($"[Mascot] server: {serverUrl} / audio: {_fetcher.BaseUrl}/audio/");
            _client.Start();
            _nextTickAt = Time.realtimeSinceStartup + TickIntervalSeconds;
        }

        private void Update()
        {
            if (_shuttingDown) return;

            // ack の間引き送出と、無受信 watchdog
            _client?.Tick();

            // ★ **下の間引き（TickIntervalSeconds）に乗せないこと。** 判定は加算と比較だけなので
            //   毎フレームで足りるし、間引きに乗せると Resume が最大1秒遅れる
            if (_idleGate != null)
            {
                ApplyIdle(_idleGate.Tick(IdleNowMs(), _player == null ? 0 : _player.ActiveCount, InFlightCount()));
            }

            if (Time.realtimeSinceStartup < _nextTickAt) return;
            _nextTickAt = Time.realtimeSinceStartup + TickIntervalSeconds;
            Dispatch(PlaybackEvent.Tick());
        }

        /// <summary>
        /// 後始末の best-effort。
        ///
        /// ★ <b>ack を投げ切るのはここではない</b>（→ <see cref="OnWantsToQuit"/>）。
        ///   ここはシーンのアンロード、Editor のドメインリロード、
        ///   <c>wantsToQuit</c> を保留できない経路の受け皿。
        /// </summary>
        private void OnDestroy()
        {
            Application.wantsToQuit -= OnWantsToQuit;
            _shuttingDown = true;
            _player?.StopAll();
            foreach (var handle in _handles.Values)
            {
                _player?.Discard(handle);
            }
            _handles.Clear();

            var client = _client;
            _client = null;
            if (client != null) _ = client.CloseAsync();
        }

        private void OnConnected()
        {
            _warnedBadFrame = false;
            _audioDeclarationChecked = false;
            Dispatch(PlaybackEvent.Connected());
        }

        private void OnFrame(string raw)
        {
            SpeechFrame frame;
            bool audioDeclared;
            if (!SpeechFrameParser.TryParse(raw, out frame, out audioDeclared))
            {
                // 知らない形は捨てる。接続は切らない
                if (!_warnedBadFrame)
                {
                    _warnedBadFrame = true;
                    Debug.LogWarning("[Mascot] 読めないフレームを捨てました");
                }
                return;
            }

            // ★ 読めたフレームで判定すること。最初のフレームが読めなかったら次で見る
            if (!_audioDeclarationChecked)
            {
                _audioDeclarationChecked = true;
                if (!audioDeclared)
                {
                    Debug.LogWarning("[Mascot] サーバーのフレームに audio がありません（#29 より前のサーバー？）");
                    Debug.LogWarning("[Mascot] 音声は鳴らず、すべての発話が無音のまま ack されます");
                }
            }

            Dispatch(PlaybackEvent.Received(frame));
        }

        /// <summary>
        /// ゲートの指示を実行する。
        ///
        /// ★ <b>ここから例外を出さないこと。</b> <see cref="Dispatch"/> は
        ///   <c>foreach (var command in Reduce(...)) Execute(command)</c> で、この呼び出しは
        ///   <c>FetchAudio</c> の処理の中にいる。例外が抜けると<b>そのバッチの残りのコマンド
        ///   （<c>Ack</c> / <c>Play</c> / <c>DiscardAudio</c>）が全部落ち</b>、
        ///   <c>FetchAudioAsync</c> も始まらないので head が <c>Pending</c> のまま
        ///   in-flight 無しになる —— <b>キューが恒久停止する</b>。
        /// </summary>
        private void ApplyIdle(IdleAction action)
        {
            if (_player == null) return;
            switch (action)
            {
                case IdleAction.Suspend:
                    try
                    {
                        _player.SuspendOutput();
                        Debug.Log("[Mascot] 無音が続いたのでオーディオ出力を止めました");
                    }
                    catch (Exception e)
                    {
                        // ★ 手放せなかったのに「手放した」状態が残ると実態とズレる。かといって
                        //   状態だけ戻すと**猶予のたびに失敗を繰り返してログが埋まる**ので、
                        //   一度失敗したら機能ごと止める。掴んだままになるだけで発話は無事
                        if (_idleGate != null) _idleGate.Enabled = false;
                        Debug.LogWarning("[Mascot] オーディオ出力を止められませんでした。" +
                                         "以後この機能を無効にします: " + e.Message);
                    }
                    break;

                case IdleAction.Resume:
                    try
                    {
                        _player.ResumeOutput();
                        Debug.Log("[Mascot] オーディオ出力を掴み直しました");
                    }
                    catch (Exception e)
                    {
                        // ★ **握りつぶして続行する。** ゲートの _suspended は既に false へ
                        //   倒れている（Wake() が Resume を返す前に落としている）ので、
                        //   状態は「掴んでいる」側にある。次の発話でもう一度 resume を試す
                        Debug.LogWarning("[Mascot] オーディオ出力を掴み直せませんでした: " + e.Message);
                    }
                    break;
            }
        }

        /// <summary>
        /// アイドル判定の時計。
        ///
        /// ★ <b>壁時計（<c>DateTimeOffset.UtcNow</c>）を使わないこと。</b> 猶予は差分でしか
        ///   見ないので、時計が巻き戻ると<b>手放したまま戻らない</b>（無音が続く）。
        ///   <c>realtimeSinceStartup</c> は単調。
        /// </summary>
        private static long IdleNowMs()
        {
            return (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
        }

        /// <summary>
        /// キューに残っていて<b>まもなく鳴る</b>件数。孤児を含める —— 採番のやり直しで
        /// <c>Items</c> から外れた再生中の音がそこにいる（契約1）。
        ///
        /// ★★ <b>時計を取り違えないこと。</b> <c>RetryAfter</c> は <see cref="Dispatch"/> と同じ
        ///   <b>壁時計</b>（Unix epoch ミリ秒 ≈ 1.7兆）で置かれる。アイドル判定の
        ///   <see cref="IdleNowMs"/> は<b>単調時計</b>（起動からの経過 ≈ 数万）で桁が違うので、
        ///   そちらで比較すると<b>全 item が「停車中」に見えて常に手放し、1文目の頭が切れる</b>。
        ///   だからここで壁時計を取る（引数で受け取らない）。
        /// </summary>
        private int InFlightCount()
        {
            if (_state == null) return 0;

            var wallNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var count = _state.Orphans.Count;
            foreach (var item in _state.Items.Values)
            {
                if (IsParked(item, wallNow)) continue;
                count++;
            }
            return count;
        }

        /// <summary>
        /// 503 のバックオフで停車中か。
        ///
        /// ★ <b>503 は意図的に <c>Attempts</c> を消費しない</b>（→ <c>QueueItem.RetryAfter</c>）ので、
        ///   合成エンジンが落ちている間、item は <c>Pending</c> + <c>RetryAfter</c> のまま
        ///   <b>永久に <c>Items</c> に残る</b>。これを「まもなく鳴る」と数えると、
        ///   <b>無音がいちばん長く続く状況で出力デバイスを掴みっぱなしになる</b> ——
        ///   この機能がいちばん得をするはずの場面で効かない。
        ///
        /// ★ <c>public static</c> なのはテストで固定するため（private では固定できない）。
        /// </summary>
        public static bool IsParked(QueueItem item, long wallNowMs)
        {
            return item != null && item.Status == ItemStatus.Pending && wallNowMs < item.RetryAfter;
        }

        private void Dispatch(PlaybackEvent ev)
        {
            if (_state == null) return;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var command in PlaybackQueue.Reduce(_state, ev, now)) Execute(command);
        }

        private void Execute(PlaybackCommand command)
        {
            switch (command.Kind)
            {
                case PlaybackCommandKind.FetchAudio:
                    // ★ **再生の直前ではなくここで掴み直す。** GET はサーバーに合成させるので
                    //   数百ms〜数秒かかり、先読みのぶんだけ再生よりさらに手前で走る。
                    //   デバイスの掴み直し（Bluetooth なら A2DP の張り直し）はその裏に隠れる
                    if (_idleGate != null) ApplyIdle(_idleGate.NoteWorkIncoming());
                    _ = FetchAudioAsync(command.Epoch, command.Seq, command.Path);
                    break;

                case PlaybackCommandKind.Play:
                    _ = PlayAsync(command.Epoch, command.Seq, command.Audio);
                    break;

                case PlaybackCommandKind.Ack:
                    _client?.Ack(command.Seq, command.EpochId);
                    break;

                case PlaybackCommandKind.DropPendingAck:
                    _client?.DropPendingAck();
                    break;

                case PlaybackCommandKind.DiscardAudio:
                    DiscardAudio(command.Epoch, command.Seq, command.Audio);
                    break;

                case PlaybackCommandKind.Log:
                    Debug.Log("[Mascot] " + command.Message);
                    break;

                case PlaybackCommandKind.Warn:
                    Debug.LogWarning("[Mascot] " + command.Message);
                    break;
            }
        }

        private async Task FetchAudioAsync(int epoch, long seq, string path)
        {
            AudioFetchResult result;
            try
            {
                result = await _fetcher.FetchAsync(path);
            }
            catch (Exception e)
            {
                // FetchAsync は自分で握るので通常ここには来ない。来たら試行回数を消費する側に倒す
                Dispatch(PlaybackEvent.AudioFailed(epoch, seq, e.Message));
                return;
            }

            if (_shuttingDown) return;

            // ★ 503 と 404 を「失敗」に混ぜないこと。混ぜると SynthesisAttempts が数 ms で
            //   燃え尽き、エンジンが落ちているだけでバックログが全部捨てられる
            switch (result.Kind)
            {
                case AudioFetchKind.Unavailable:
                    Dispatch(PlaybackEvent.AudioUnavailable(epoch, seq, result.Reason));
                    return;
                case AudioFetchKind.Gone:
                    Dispatch(PlaybackEvent.AudioGone(epoch, seq, result.Reason));
                    return;
                case AudioFetchKind.Failed:
                    Dispatch(PlaybackEvent.AudioFailed(epoch, seq, result.Reason));
                    return;
            }

            string error;
            var handle = _player.Prepare(result.Wav, $"speech-{epoch}-{seq}", out error);
            if (handle == null)
            {
                Dispatch(PlaybackEvent.AudioFailed(epoch, seq, error ?? "WAV を読めませんでした"));
                return;
            }

            // ★ 状態機械へ渡す前に手元にも持つこと。解放の対象を取り違えないための台帳
            _handles[Key(epoch, seq)] = handle;
            Dispatch(PlaybackEvent.AudioReady(epoch, seq, handle));
        }

        private async Task PlayAsync(int epoch, long seq, object audio)
        {
            var error = await _player.PlayAsync(audio);
            if (_shuttingDown) return;

            Dispatch(error == null
                ? PlaybackEvent.Played(epoch, seq)
                : PlaybackEvent.PlaybackFailed(epoch, seq, error));
        }

        private void DiscardAudio(int epoch, long seq, object audio)
        {
            _handles.Remove(Key(epoch, seq));
            _player?.Discard(audio);
        }

        /// <summary>
        /// ★ スキームまで見ること。<c>Uri.TryCreate</c> は <c>http://…</c> も
        ///   <c>file:///…</c> も通すが、<c>ClientWebSocket</c> は <c>ws</c> / <c>wss</c> しか繋げない。
        /// </summary>
        private static bool IsValidServerUrl(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return false;
            return parsed.Scheme == "ws" || parsed.Scheme == "wss";
        }

        private static string Key(int epoch, long seq)
        {
            return epoch + ":" + seq;
        }
    }
}
