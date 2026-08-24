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
        private SpeechClient _client;
        private AudioFetcher _fetcher;
        private AudioClipPlayer _player;

        /// <summary>
        /// 取得済みの音声。キーは <c>"{epoch}:{seq}"</c>。
        ///
        /// ★ <b><c>seq</c> だけをキーにしないこと。</b> 採番のやり直しを跨いだ瞬間に別の文と衝突する。
        /// ★ <b>サーバーの epoch をそのままキーに使わないこと</b>（外部由来の文字列）。
        ///   状態機械が読み替えた<b>プロセス内の連番</b>を使う。
        /// </summary>
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();

        private float _nextTickAt;
        private bool _shuttingDown;

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
            if (targetFrameRate > 0) Application.targetFrameRate = targetFrameRate;

            if (audioSource == null) audioSource = GetComponent<AudioSource>();
            if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
        }

        private void Start()
        {
            var options = new PlaybackOptions
            {
                Lookahead = lookahead,
                MaxAgeMs = speechMaxAgeMs,
                // サーバーは自分のキューにある分しか再送できないので、その上限を覚えていれば足りる
                SeenCapacity = 512,
            };
            _state = new PlaybackState(options);

            _player = new AudioClipPlayer(audioSource);
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

            if (Time.realtimeSinceStartup < _nextTickAt) return;
            _nextTickAt = Time.realtimeSinceStartup + TickIntervalSeconds;
            Dispatch(PlaybackEvent.Tick());
        }

        private void OnDestroy()
        {
            _shuttingDown = true;
            _player?.StopAll();
            foreach (var clip in _clips.Values)
            {
                if (clip != null) Destroy(clip);
            }
            _clips.Clear();

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
                    _ = FetchAudioAsync(command.Epoch, command.Seq, command.Path);
                    break;

                case PlaybackCommandKind.Play:
                    _ = PlayAsync(command.Epoch, command.Seq, command.Audio as AudioClip);
                    break;

                case PlaybackCommandKind.Ack:
                    _client?.Ack(command.Seq, command.EpochId);
                    break;

                case PlaybackCommandKind.DropPendingAck:
                    _client?.DropPendingAck();
                    break;

                case PlaybackCommandKind.DiscardAudio:
                    DiscardAudio(command.Epoch, command.Seq, command.Audio as AudioClip);
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
            var clip = WavDecoder.Decode(result.Wav, $"speech-{epoch}-{seq}", out error);
            if (clip == null)
            {
                Dispatch(PlaybackEvent.AudioFailed(epoch, seq, error ?? "WAV を読めませんでした"));
                return;
            }

            // ★ 状態機械へ渡す前に手元にも持つこと。Destroy の対象を取り違えないための台帳
            _clips[Key(epoch, seq)] = clip;
            Dispatch(PlaybackEvent.AudioReady(epoch, seq, clip));
        }

        private async Task PlayAsync(int epoch, long seq, AudioClip clip)
        {
            var error = await _player.PlayAsync(clip);
            if (_shuttingDown) return;

            Dispatch(error == null
                ? PlaybackEvent.Played(epoch, seq)
                : PlaybackEvent.PlaybackFailed(epoch, seq, error));
        }

        private void DiscardAudio(int epoch, long seq, AudioClip clip)
        {
            _clips.Remove(Key(epoch, seq));
            if (clip != null) Destroy(clip);
        }

        private static string Key(int epoch, long seq)
        {
            return epoch + ":" + seq;
        }
    }
}
