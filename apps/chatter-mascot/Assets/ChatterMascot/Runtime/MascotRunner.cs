using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatterMascot.Audio;
using ChatterMascot.Net;
using ChatterMascot.Playback;
using ChatterMascot.Protocol;
using ChatterMascot.Settings;
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
        ///
        /// ★ <b>デスクトップでは設定パネルの <c>display.frameRate</c>（#88）がこの値を上書きする</b>
        ///   —— <see cref="SetTargetFrameRate"/> 経由で、<c>Awake</c> の後（設定を読み終えたところ）
        ///   から効く。<b>Android / XR には設定パネルが無い</b>ので、この <c>[SerializeField]</c> の
        ///   既定（＝ <c>settings.json</c> を読めなかったときの既定でもある。→
        ///   <c>Settings.SettingsMapping.DefaultFrameRate</c>）がそのまま使われる。
        ///   ヘッドセットのリフレッシュレートに合わせる話（#25）が入るまではここが権威。
        /// </summary>
        [Header("表示")]
        [Tooltip("フレームレートの上限。常駐アプリなので電力に直接効く。0 以下なら制限しない。" +
                 "デスクトップでは設定パネルの display.frameRate（SetTargetFrameRate）がこれを上書きする")]
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

        /// <summary>
        /// 再生の開始が実際に音になるまでのラグ。エンベロープの索引をこのぶん戻す。
        ///
        /// ★ <b>これは macOS（<c>afplay</c>）用の値。</b> <c>Process.Start</c> は音が出るより前に
        ///   返るので、補正しないと<b>口が音より先に動く</b>。Unity 内蔵オーディオの実装
        ///   （<c>AudioClipPlayer</c>）では 0 が正しい —— 原理的には発話ごとに持つべきだが、
        ///   <c>MascotRunner</c> は <see cref="ISpeechPlayer"/> 型しか持たない。
        ///   Android を入れるとき（#25）に <c>ILipSyncSource</c> へ移す。
        ///
        /// ★ <b>負の側へ倒さないこと。</b> 口が音より先に動くより、遅れるほうが自然に見える。
        ///
        /// ★ <b>既定 120 は実測値</b>（2026-08-29 / macOS 26.6.2 / 内蔵スピーカー）。
        ///   CoreAudio の <c>kAudioDevicePropertyDeviceIsRunningSomewhere</c> を 2ms 間隔で
        ///   ポーリングして、<c>Process.Start</c> から出力デバイスが動き出すまでを直接測ると
        ///   <b>中央値 116ms</b>（n=5、100〜134ms）だった。
        /// ★★ <b><c>PlayAsync</c> の較正ログ（実時間 − WAV の長さ）をそのまま入れないこと。</b>
        ///   あれは <b>約 470ms</b> を示すが、内訳は<b>起動ラグ 116ms + 終了処理 357ms</b>で、
        ///   欲しいのは前者だけ。後者を足すと口が音より 0.35 秒**遅れる**。
        /// ★ <b>Bluetooth ではもっと大きい。</b> 上の実測は内蔵スピーカー。A2DP の遅延は
        ///   デバイス側で更に乗るので、そこが気になったら Inspector で上げる。
        ///   <b>秒数を仕様として扱わないこと。</b>
        /// </summary>
        [Tooltip("再生開始から実際に音が出るまでのラグ（ミリ秒）。macOS の afplay 用。0 で補正しない")]
        [SerializeField] private int lipSyncOffsetMs = 120;

        /// <summary>
        /// 発話中だけフレームレートを上げる逃げ道。<b>既定 0（変えない）。</b>
        ///
        /// ★ <b>常時上げないこと。</b> 常駐アプリの電力設計（#55）を壊す。エンベロープの刻み
        ///   （20ms）は表示（30fps = 33.3ms）と割り切れないが、<c>SpeakingSet.Mouth</c> が
        ///   <b>区間の最大</b>を取るので 30fps でも立ち上がりは落ちない。ここを開けるのは
        ///   「それでも口が階段状に見える」と実機で判断したときだけ。
        /// </summary>
        [Tooltip("発話中だけのフレームレート上限。0 以下なら変えない（常駐アプリの電力設計に直接効く）")]
        [SerializeField] private int speakingFrameRate = 0;

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
        /// いま鳴っている発話の <c>kind</c> / <c>emotion</c>。<b>読むだけ</b>。
        /// <see cref="_speaking"/> はフィールド初期化子で作るので、<c>Start</c> 前でも
        /// <c>null</c> にならない（そのまま <c>false</c> が返る）。
        ///
        /// ★ <b><c>false</c> のとき <c>Assistant</c> / <c>Neutral</c> に倒すのは
        ///   <see cref="SpeakingSet.TryGetFace"/> 側の契約</b>で、<c>VrmCharacter.LateUpdate</c> が
        ///   それに乗っている（呼び出し側で <c>Speaking ? kind : 既定</c> と書き直していない）。
        ///
        /// ★ <b><see cref="PlaybackState"/> を丸ごと公開しないこと。</b> 以前は
        ///   <c>public PlaybackState State</c> で公開していたが、<c>PlaybackState</c> のフィールドは
        ///   すべて <c>public</c> で、<c>Items</c> は可変 <c>Dictionary</c>、<c>Seen</c> /
        ///   <c>SeenOrder</c> は二重読み上げ防止の要 —— 将来の消費者が1行書き換えるだけで
        ///   <c>CLAUDE.md</c>「絶対に守ること」6（<c>seq</c> / <c>epoch</c> の契約）を静かに破れる。
        ///   必要な情報だけを返すメソッドにして、書き込む口を作らない。
        /// ★ <b>書き込むと <see cref="PlaybackQueue"/> の状態機械が壊れる。</b>
        ///   コマンドを増やす口ではない。
        ///
        /// ★★ <b>#58 で <c>SpeakingView</c> から移した。</b> あちらは <c>PlaybackState.Items</c> を
        ///   走査していたが、<c>Orphans</c> は音声ハンドルしか持たず <c>Record</c> を持たないので、
        ///   <b>孤児が鳴っている間は常に <c>false</c></b> だった。<see cref="SpeakingSet"/> は
        ///   再生開始時に写し取るので、その穴が閉じている。
        /// </summary>
        public bool TryGetSpeaking(out SpeechKind kind, out Emotion emotion)
        {
            // ★ 引数の順が逆（TryGetFace は emotion が先）。呼び出し側の並びは
            //   VrmCharacter が使っているものなので、こちらで受け替える
            return _speaking.TryGetFace(out emotion, out kind);
        }

        /// <summary>
        /// 再生音量（<b>0.0〜1.0</b>。既定 1.0）。設定パネル（#76）が書き、次の発話から効く。
        /// 画面には 0〜100% で出る（→ <c>Settings.SettingDisplay.Percent</c>）。
        ///
        /// ★★ <b>ミュートの代わりにしないこと。</b> <c>0</c> にしても
        ///   <c>afplay</c> のプロセスは起動し、実時間ぶん走る。ミュートは
        ///   <see cref="Audio.MutedSpeechPlayer"/> が「声だけ消す」形で担う
        ///   （ack は通常経路のまま出す）。
        ///
        /// ★ <b>効かせ方がプラットフォームで違う。</b> macOS は <c>afplay -v</c>
        ///   （getter を再生側へ渡してある）、Android は<b>テンプレートの
        ///   <see cref="AudioSource.volume"/></b>（voice は発話ごとに作られるので、
        ///   ここを書けば次の発話から効く）。
        ///
        /// ★★ <b>上限が 1.0 なのは、その違いを設定に持ち込まないため</b>
        ///   （→ <c>Settings.SettingsMapping.VolumeMax</c>）。<see cref="AudioSource.volume"/> は
        ///   Unity 側で 0〜1 にクランプされるので、1.0 超えは Android で黙って no-op になる。
        /// </summary>
        public float Volume
        {
            get { return _volume; }
            set
            {
                _volume = SettingsMapping.Normalize(
                    value, SettingsMapping.VolumeMin, SettingsMapping.VolumeMax, SettingsMapping.VolumeStep);
                // ★ Android 側はテンプレートに書く。macOS 側は getter 越しに読まれるので何もしない
                if (audioSource != null) audioSource.volume = _volume;
            }
        }

        /// <summary>
        /// 接続先（<c>ws://host:port</c>）。設定パネル（#76）が制御 API の口を導くのに使う。
        ///
        /// ★ 起動引数（<c>-serverUrl</c>）で上書きされた後の<b>実際の値</b>を返すこと。
        ///   <c>[SerializeField]</c> をそのまま読むと、引数で別のサーバーを指したときに
        ///   設定パネルだけ元のサーバーを見に行く。
        /// ★★ <b>その約束は <c>Awake</c> で上書きすることで守られている</b>
        ///   （→ <see cref="ResolveServerUrl"/>）。読み手（<c>StatusItemBridge</c>）は
        ///   <c>Start</c> に居るので、上書きを <c>Start</c> でやると<b>順序が未規定になる</b>。
        /// </summary>
        public string ServerUrl
        {
            get { return serverUrl; }
        }

        /// <summary>
        /// 設定パネルのテスト音声を鳴らす（#76）。失敗したら理由、成功なら <c>null</c>。
        ///
        /// ★ <b>通常の再生経路をそのまま通す。</b> 別経路で鳴らすと、
        ///   「テストは鳴るのに本番が鳴らない」（またはその逆）を作ってしまう。
        ///   ★ その帰結として、<b>ミュート中は鳴らない</b>（<c>MutedSpeechPlayer</c> が
        ///   声だけ消す）。呼び出し側がミュート中である旨を出すこと。
        ///
        /// ★ <b>キューには載せない。</b> 配信された発話ではないので <c>seq</c> も ack も無い。
        ///   口も表情も動かない（<c>BeginSpeaking</c> を通さない）——
        ///   確かめたいのは「声と速さ」なので、それで足りる。
        /// </summary>
        public async Task<string> PlayPreviewAsync(byte[] wav)
        {
            if (_player == null) return "再生の準備ができていません";
            if (wav == null || wav.Length == 0) return "音声が空です";

            string error;
            var handle = _player.Prepare(wav, "preview-" + DateTime.UtcNow.Ticks, out error);
            if (handle == null) return string.IsNullOrEmpty(error) ? "音声を用意できませんでした" : error;

            return await _player.PlayAsync(handle);
        }

        /// <summary>
        /// 一時ミュートの状態。<b>読み書きの両方に使う</b>（ステータスバーのメニューと
        /// グローバルショートカットから切り替わる）。
        /// </summary>
        public MuteState Mute
        {
            get { return _mute; }
        }

        /// <summary>
        /// 区間 <c>[from, to]</c>（<c>Time.realtimeSinceStartupAsDouble</c> の秒）における
        /// 口の開きの元になる値（<b>生の RMS。ゲイン前</b>）。
        ///
        /// ★ <b>点ではなく区間で問い合わせること。</b> エンベロープの刻み（20ms）は
        ///   表示（30fps = 33.3ms）と割り切れないので、点サンプリングすると
        ///   <b>4割のフレームを読み飛ばす</b>。<c>from</c> を持つのは呼び出し側
        ///   （<c>MouthTracker</c>）—— ここに持たせると<b>このメソッドが冪等でなくなる</b>。
        /// </summary>
        public float Mouth(double from, double to)
        {
            return _speaking.Mouth(from, to, lipSyncOffsetMs);
        }

        private SpeechClient _client;
        private AudioFetcher _fetcher;
        private ISpeechPlayer _player;
        private AudioIdleGate _idleGate;

        /// <summary>
        /// いま鳴っている発話。<b>フィールド初期化子で作ること</b> —— <c>VrmCharacter</c> は
        /// フレーム1から <see cref="TryGetSpeaking"/> / <see cref="Mouth"/> を呼ぶので、
        /// <c>Start()</c> を待つと呼び出し側に null チェックが要る。
        /// </summary>
        private readonly SpeakingSet _speaking = new SpeakingSet();

        /// <summary>
        /// 一時ミュート（#75）。<b>ここが所有者</b>で、ステータスバー（<c>Desktop</c>）は
        /// <see cref="Mute"/> 越しに触る。
        ///
        /// ★ <b>フィールド初期化子で作ること。</b> <c>Start</c> の前に
        ///   <c>StatusItemBridge</c> が読みに来る（実行順は保証されない）。
        /// </summary>
        private readonly MuteState _mute = new MuteState();
        private float _volume = 1f;

        /// <summary>
        /// <see cref="speakingFrameRate"/> の借用。<b>1本だけ取り回す。</b>
        ///
        /// ★ <b>発話ごとに借りて返す形にしないこと。</b> <c>FrameRateBudget</c> は深さで
        ///   数えるので動きはするが、返し忘れが1回でもあると<b>恒久的に上げたままになり、
        ///   常駐アプリの電力設計（#55）が黙って死ぬ</b>。
        /// </summary>
        private IDisposable _speakingBoost;

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

        /// <summary>
        /// <c>Application.Quit()</c> を呼び直す上限と間隔。
        ///
        /// ★ <b>「効かないこと」を観測するための仕組み。</b>
        ///   <a href="https://github.com/schwarz9791/chatter-agent/issues/68">#68</a> の仮説
        ///   （<c>wantsToQuit</c> で <c>false</c> を返した後の <c>Quit()</c> が macOS で
        ///   無視されている）が当たっているかは、<b>再試行のログが出るかどうかでしか分からない</b>。
        ///   当たっていれば手当てはネイティブ側
        ///   （<a href="https://github.com/schwarz9791/chatter-agent/issues/75">#75</a> の
        ///   <c>replyToApplicationShouldTerminate:</c>）になるが、
        ///   <b>原因が確定する前にネイティブを足すと、効いた理由が分からなくなる</b>。
        /// </summary>
        private const int QuitMaxAttempts = 3;
        private const double QuitRetryIntervalSeconds = 2;

        private float _nextTickAt;
        private bool _shuttingDown;
        private bool _quitRequested;

        /// <summary>後始末が終わり、あとは <c>Application.Quit()</c> を呼ぶだけの状態。</summary>
        private bool _quitPending;

        /// <summary>
        /// <b>実機確認専用。</b> 最初の終了要求を1回だけ強制的に保留する（<c>-quitProbe</c>）。
        ///
        /// ★ <b>この経路は、これが無いと確かめられない。</b> 保留が起きるのは未 ack が
        ///   残っている数十 ms の窓（<c>AckFlushMs</c> 20ms + <c>Tick</c> の1フレーム）だけで、
        ///   <b>サーバーと実際の発話が無いと再現できない</b>。
        ///   <a href="https://github.com/schwarz9791/chatter-agent/issues/68">#68</a> の仮説
        ///   （<c>wantsToQuit</c> で <c>false</c> を返した後の <c>Quit()</c> が効かない）が
        ///   <b>長いあいだ未検証のまま残っていたのはこのため</b>。
        ///
        /// ★ <b>既定では絶対に立てないこと。</b> 立てると未 ack が無くても終了が1フレーム遅れる。
        /// </summary>
        private bool _quitProbe;
        private int _quitAttempts;
        private double _lastQuitAttemptAt;
        private bool _quitGaveUp;

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

            // ★ Awake で読むこと。Start は serverUrl が不正だと最後まで走らない
            _quitProbe = CommandLine.Flag("-quitProbe");
            if (_quitProbe) Debug.Log("[Mascot] -quitProbe: 最初の終了要求を1回だけ強制的に保留します");

            ResolveServerUrl();
        }

        /// <summary>
        /// 表示のフレームレート上限を変える（設定パネル、#88）。<see cref="targetFrameRate"/> の doc。
        ///
        /// ★ <b><c>Application.targetFrameRate</c> を直接書かないこと</b>（→ <c>FrameRateBudget</c>）。
        ///   ここは「戻す先」を宣言し直すだけで、VRM 読み込み中などの一時的な引き上げ
        ///   （<see cref="FrameRateBudget.Boost"/>）が効いている間は、借用が終わってから反映される。
        /// </summary>
        public void SetTargetFrameRate(int frameRate)
        {
            targetFrameRate = frameRate;
            FrameRateBudget.SetBaseline(targetFrameRate);
        }

        /// <summary>
        /// <c>-serverUrl</c> の上書きを <see cref="serverUrl"/> へ焼く。
        ///
        /// ★★ <b><c>Start</c> ではなく <c>Awake</c> で行うこと。</b> 設定パネル（#76）は
        ///   <c>StatusItemBridge.Bridge.Start()</c> から <see cref="ServerUrl"/> を読んで
        ///   <c>CoreConfigClient</c> の接続先を<b>1回きり</b>捕まえる。あちらも <c>Start</c> なので
        ///   <b>2つの <c>Start</c> の相対順序は保証されない</b>（どちらにも
        ///   <c>[DefaultExecutionOrder]</c> は付いていない）。先に走られると
        ///   <c>[SerializeField]</c> の既定値が焼かれ、<b>再生は正しいサーバーなのに設定パネルだけ
        ///   別のサーバーを読み書きする</b>状態がセッション中ずっと続く。
        ///   <c>Bridge</c> は <c>RuntimeInitializeOnLoadMethod(AfterSceneLoad)</c> で生えるので、
        ///   <b>シーンの <c>Awake</c> はすべて終わった後</b>に <c>Start</c> が来る ——
        ///   ここへ移せば順序が決まる。
        ///
        /// ★ <b>検証（<see cref="IsValidServerUrl"/>）は <c>Start</c> のまま。</b> あちらは
        ///   「<c>_client</c> を作れるか」の話で、読み手の順序とは別の関心事。
        /// </summary>
        private void ResolveServerUrl()
        {
            var overridden = CommandLine.Argument("-serverUrl");
            if (string.IsNullOrEmpty(overridden)) return;

            Debug.Log($"[Mascot] serverUrl をコマンドラインで上書きします: \"{overridden}\"");
            serverUrl = overridden;
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
            var pending = _client != null && _client.HasPendingWork;

            // ★ **実際の未 ack と、probe による強制を混ぜないこと。** 保留経路は probe でしか
            //   通らないので、混ぜると**この経路を通る実行はすべてログが嘘**になり、
            //   後から読む人が本物の未 ack と区別できない
            var forced = _quitProbe;
            // 1回だけ。2周目まで残すと ShouldDefer の「必ず通す」が試せない
            _quitProbe = false;

            var defer = ShutdownPolicy.ShouldDefer(pending || forced, _quitRequested);

            // ★ **必ず1行残すこと。** 保留したのか素通りしたのかは、ここでしか分からない。
            //   #68 の切り分けはこの行から始まる
            Debug.Log($"[Mascot] 終了要求: 未 ack={(pending ? "あり" : "なし")}" +
                      $"{(forced ? "（probe による強制）" : "")} " +
                      $"2周目={_quitRequested} → {(defer ? "保留します" : "通します")}");

            _quitRequested = true;
            if (!defer) return true;

            _ = ShutdownThenQuitAsync();
            return false;
        }

        /// <summary>
        /// 後始末が終わったら <c>Application.Quit()</c> を呼ぶ。効かなければ呼び直す。
        ///
        /// ★ <b><c>wantsToQuit</c> の継続からその場で呼ばないこと。</b>
        ///   <c>applicationShouldTerminate</c> に <c>NSTerminateCancel</c> を返した直後の
        ///   AppKit の状態とぶつかりうる、というのが #68 の仮説。フレームを1つ跨ぐ。
        /// </summary>
        private void PumpQuit()
        {
            if (!_quitPending) return;

            var now = Time.realtimeSinceStartupAsDouble;
            if (_quitAttempts == 0)
            {
                CallQuit(now);
                return;
            }

            if (ShutdownPolicy.ShouldRetryQuit(
                    now, _lastQuitAttemptAt, _quitAttempts, QuitMaxAttempts, QuitRetryIntervalSeconds))
            {
                CallQuit(now);
                return;
            }

            // ★ 撃ち止め。無限に呼び直すとログが洪水になり、別の原因（そもそも Quit() に
            //   到達していない）を隠してしまう
            if (_quitAttempts >= QuitMaxAttempts && !_quitGaveUp)
            {
                _quitGaveUp = true;
                Debug.LogError($"[Mascot] Application.Quit() を {QuitMaxAttempts} 回呼んでも終了しません。" +
                               "終了要求が OS 側で無視されている可能性があります (#68)");
            }
        }

        private void CallQuit(double now)
        {
            _quitAttempts++;
            _lastQuitAttemptAt = now;
            Debug.Log($"[Mascot] Application.Quit() を呼びました (試行 {_quitAttempts})");
            Application.Quit();
        }

        private async Task ShutdownThenQuitAsync()
        {
            _shuttingDown = true;

            var client = _client;
            _client = null;
            if (client != null)
            {
                var startedAt = Time.realtimeSinceStartupAsDouble;
                // ★ **この詳細は保留した経路でしか出ない。** 通常経路は投げ切るものが無いので
                //   閉じる過程を残す意味が無い。切り分けの入口（上の「終了要求」）は
                //   **両方の経路で出る**
                Debug.Log("[Mascot] 終了処理: 接続を閉じます");
                try
                {
                    // 予算内で閉じ切る。返らない相手のためにアプリを終了させないことはしない
                    var closing = client.CloseAsync();
                    var finished = await Task.WhenAny(closing, Task.Delay(ShutdownBudgetMs));
                    var ms = (int)((Time.realtimeSinceStartupAsDouble - startedAt) * 1000);
                    if (ReferenceEquals(finished, closing))
                    {
                        Debug.Log($"[Mascot] 終了処理: 接続を閉じました ({ms}ms)");
                    }
                    else
                    {
                        Debug.LogWarning($"[Mascot] 終了処理: {ShutdownBudgetMs}ms の予算を使い切りました。" +
                                         "閉じ切らずに進みます");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Mascot] 終了処理で例外が出ました: " + e.Message);
                }
            }

            // ★ **ここで Application.Quit() を直接呼ばない**（→ <see cref="PumpQuit"/>）
            _quitPending = true;
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
            //
            // ★★ **上書きそのものは Awake で済ませてある**（→ ResolveServerUrl）。ここに残すと、
            //   同じ Start パスに居る StatusItemBridge が**先に ServerUrl を読みうる**。
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

            // ★ ミュートは音の層で実装する（→ MutedSpeechPlayer）。PlaybackQueue には触らない。
            //   包むのはここ1箇所で、CanSuspendOutput も ActiveCount も委譲されるので
            //   下の AudioIdleGate の判定は何も変わらない
            _player = new MutedSpeechPlayer(SpeechPlayerFactory.Create(audioSource, () => _volume), _mute);
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
            // ★ **_shuttingDown の早期 return より手前に置くこと。** 後始末が終わってから
            //   Application.Quit() を呼び直す経路なので、下に置くと1回も走らない
            PumpQuit();

            if (_shuttingDown) return;

            // ack の間引き送出と、無受信 watchdog
            _client?.Tick();

            // ★ **下の間引き（TickIntervalSeconds）に乗せないこと。** 判定は加算と比較だけなので
            //   毎フレームで足りるし、間引きに乗せると Resume が最大1秒遅れる
            if (_idleGate != null)
            {
                ApplyIdle(_idleGate.Tick(IdleNowMs(), _player == null ? 0 : _player.ActiveCount, InFlightCount()));
            }

            // ★ アイドル判定と同じ理由で毎フレーム見る（比較1回）。ここを間引くと
            //   発話の頭で上げ損ねる
            UpdateSpeakingFrameRate();

            if (Time.realtimeSinceStartup < _nextTickAt) return;
            _nextTickAt = Time.realtimeSinceStartup + TickIntervalSeconds;
            Dispatch(PlaybackEvent.Tick());
        }

        /// <summary>
        /// 発話中だけフレームレートを上げる（<see cref="speakingFrameRate"/> が 0 以下なら何もしない）。
        ///
        /// ★ <c>FrameRateBudget.Boost</c> は「baseline 以下の上げ方」を借りたことにしないので、
        ///   既定 0 では <c>Handle.NoOp</c> が返るだけ。分岐を足す必要は無い。
        /// ★ <b><c>Application.targetFrameRate</c> を直接書かないこと</b>（→ <c>FrameRateBudget</c>）。
        /// </summary>
        private void UpdateSpeakingFrameRate()
        {
            var speaking = _speaking.Count > 0;
            if (speaking)
            {
                if (_speakingBoost == null) _speakingBoost = FrameRateBudget.Boost(speakingFrameRate);
                return;
            }

            ReleaseSpeakingFrameRate();
        }

        /// <summary>
        /// 借りているフレームレートを返す。
        ///
        /// ★★ <b>ここに <c>StopAll</c> / <c>EndAll</c> / <c>Discard</c> を降ろさないこと。</b>
        ///   <see cref="OnDisable"/> は GameObject の非アクティブ化でも走るので、<b>音を止めてしまう</b>。
        ///   後始末の本体は <see cref="OnDestroy"/> のまま。
        ///
        /// ★ <b>返却を <see cref="OnDestroy"/> だけに任せないこと</b>（<c>VrmStage.OnDisable</c> が先例）。
        ///   <c>speakingFrameRate</c> を開けた状態で発話中にコンポーネントを無効化 / GameObject を
        ///   非アクティブ化すると、<see cref="Update"/> が止まって
        ///   <see cref="UpdateSpeakingFrameRate"/> が返却できず、<see cref="OnDestroy"/> も走らない。
        ///   <c>FrameRateBudget</c> の深さが増えたままになり、<b>恒久的に上げたままで
        ///   常駐アプリの電力設計（#55）が黙って死ぬ</b>。
        /// ★ <c>Handle.Dispose</c> は冪等なので <see cref="OnDestroy"/> 側は残してよい。
        /// </summary>
        private void OnDisable()
        {
            ReleaseSpeakingFrameRate();
        }

        private void ReleaseSpeakingFrameRate()
        {
            if (_speakingBoost == null) return;
            _speakingBoost.Dispose();
            _speakingBoost = null;
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
            // ★ StopAll の直後に落とすこと。残すと VrmCharacter から「まだ喋っている」に見え、
            //   口が開いたままシーンが破棄される
            _speaking.EndAll();
            ReleaseSpeakingFrameRate();
            foreach (var handle in _handles.Values)
            {
                _player?.Discard(handle);
            }
            _handles.Clear();

            // ★ Discard の後に捨てること。 MutedSpeechPlayer は MuteState の購読を持つので、
            //   ここで外さないと Desktop 側（DontDestroyOnLoad で生き続ける）から
            //   死んだシーンのプレイヤーが掴まれたままになる
            (_player as IDisposable)?.Dispose();

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

        /// <summary>
        /// キューから、その <paramref name="seq"/> の発話の表情を読む。
        /// <b>読めなければ <c>Assistant</c> / <c>Neutral</c> に倒す。</b>
        ///
        /// ★★ <b>戻り値を <c>bool</c> にしないこと。</b> <c>void</c> なら
        ///   <b>呼び出し側が「読めなかったから登録を飛ばす」と書けない</b>（分岐材料が存在しない）。
        ///   「登録そのものを飛ばさないこと」というルールを、テストではなく<b>型で</b>固定している ——
        ///   飛ばすと <c>SpeakingSet</c> に載らず、<b>鳴っているのに喋っていない</b>状態になって
        ///   口も表情も体の動きも止まる。<see cref="IsParked"/> が <c>bool</c> を返すのは
        ///   分岐が目的だから、という対比で覚えること。<b><c>TryReadFace</c> に「改善」しないこと。</b>
        ///
        /// ★ <b>引数を <see cref="QueueItem"/> にしないこと。</b> <c>Items.TryGetValue</c> を
        ///   呼び出し側に残すと、<c>state == null</c> と「未知の <c>seq</c>」の2分岐がまた
        ///   private へ戻り、テストが届かなくなる。
        ///
        /// ★ <b><c>seq</c> だけで引けるのは「<c>Play</c> と同じ tick で読む」から。</b>
        ///   <c>PlaybackQueue.StartPlayback</c> は <c>state.Epoch</c> と <c>head.Record.Seq</c> を
        ///   同じ tick で組にして <c>Play</c> を積み、<see cref="Execute"/> は同期で回るので、
        ///   <c>Items[seq]</c> は必ずその head。<b><c>Play</c> を遅延実行に変えた瞬間に静かに壊れる</b>。
        ///
        /// ★ <c>public static</c> なのはテストで固定するため（<see cref="IsParked"/> と同じ扱い）。
        /// </summary>
        public static void ReadFace(PlaybackState state, long seq, out SpeechKind kind, out Emotion emotion)
        {
            kind = SpeechKind.Assistant;
            emotion = Emotion.Neutral;

            QueueItem item;
            if (state == null) return;
            if (!state.Items.TryGetValue(seq, out item)) return;
            if (item == null || item.Record == null) return;

            kind = item.Record.Kind;
            emotion = item.Record.Emotion;
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
                    // ★ **PlayAsync の中ではなくここで Begin する。** PlayAsync は同期完了する
                    //   経路（「音声のハンドルがありません」など）があり、そこから Dispatch が
                    //   このコマンドループへ再入する。入れ子の順序を読めなくしない
                    BeginSpeaking(command.Epoch, command.Seq, command.Audio);
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

        /// <summary>
        /// 「いま鳴っているもの」に登録する。<b>emotion / kind はここで写し取る。</b>
        ///
        /// ★ <b>写し取るのが要点。</b> 採番のやり直し（<c>ResetEpoch</c>）は再生中の item を
        ///   <c>Orphans</c> へ移すが、そこに残るのは<b>音声ハンドルだけで <c>Record</c> は捨てられる</b>。
        ///   参照で持つと孤児になった瞬間に表情が読めなくなる（<c>SpeakingView</c> の既知の穴）。
        /// ★ <c>Play</c> が出る時点で item は <c>Status = Playing</c> で <c>Items</c> に残っている
        ///   （<c>ConsumeHead</c> は <c>Done</c> しか消さず、<c>MarkStale</c> は <c>Playing</c> を飛ばす）。
        ///   それでも <c>Record</c> が無い場合は既定値で登録する —— <b>登録そのものを飛ばさないこと</b>。
        ///   飛ばすと「鳴っているのに喋っていない」状態になり、口も表情も体の動きも止まる。
        ///   <b>この規則は <see cref="ReadFace"/> が <c>void</c> であることで型に落としてある。</b>
        /// </summary>
        private void BeginSpeaking(int epoch, long seq, object audio)
        {
            SpeechKind kind;
            Emotion emotion;
            ReadFace(_state, seq, out kind, out emotion);

            var source = audio as ILipSyncSource;

            // ★★ ミュート中は口だけ止める。 登録そのものは飛ばさないこと ——
            //   飛ばすと表情も体の動きも止まり、「声を消した」ではなく「居なくなった」に見える
            //   （→ SpeakingSet.Begin の doc / MutedSpeechPlayer）
            if (_mute.Muted) source = null;

            _speaking.Begin(
                epoch, seq, emotion, kind,
                source == null ? null : source.Envelope,
                source == null ? LipSyncEnvelope.DefaultFrameMs : source.EnvelopeFrameMs,
                Time.realtimeSinceStartupAsDouble);
        }

        private async Task PlayAsync(int epoch, long seq, object audio)
        {
            string error;
            try
            {
                error = await _player.PlayAsync(audio);
            }
            finally
            {
                // ★ **finally で落とすこと。** _shuttingDown の早期 return より手前で落とさないと
                //   終了経路でエントリが残る。さらに PlayAsync は `_ = ` の fire-and-forget なので、
                //   実装が例外を投げたときその例外は**未観測のまま捨てられる** ——
                //   その経路で End を落とすと、**口が開きっぱなしのまま永久に固まる**
                _speaking.End(epoch, seq);
            }

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
