using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatterMascot.Net
{
    /// <summary>
    /// <c>chatter-agent-server</c> への接続。<b>フレームの解釈も判断もしない</b>
    /// （→ <c>ChatterMascot.Playback.PlaybackQueue</c>）。
    /// <c>core/src/player/client.ts</c> の移植で、繋ぐ・繋ぎ直す・ack を送る、だけを持つ。
    ///
    /// ★ <b><c>Origin</c> を付けないこと。</b> サーバーの <c>allowedOrigins</c> の既定は空で、
    ///   Origin 付きの接続はすべて拒否される。<c>ClientWebSocket</c> は既定で付けないので、
    ///   足さなければよい（ネイティブから張る限り設定は不要）。
    /// </summary>
    public sealed class SpeechClient
    {
        /// <summary>初回の再接続待ち。ここから倍々に伸ばす。</summary>
        private const int BackoffMinMs = 500;
        private const int BackoffMaxMs = 30000;

        /// <summary>
        /// これだけ繋がっていられたらバックオフをリセットする。
        ///
        /// ★ 接続した瞬間にリセットしないこと。バックプレッシャ（1013）で切られ続ける相手だと
        ///   「繋がる → すぐ切れる」が最短間隔で回り続ける。
        /// </summary>
        private const int BackoffResetAfterMs = 10000;

        /// <summary>
        /// これだけ何も受信しなければ、繋がっているとみなさず切り直す。
        ///
        /// ★ <b><c>ClientWebSocket</c> は ping コールバックを露出しない。</b> 参照実装
        ///   （<c>client.ts</c>）はサーバーの ping が90秒途切れたら繋ぎ直すが、C# では
        ///   その信号を受け取れない。代わりに「無受信」で見る。
        ///
        /// ★ <b>数十秒の無音は正常</b>（<c>AskUserQuestion</c> の直前など）なので、閾値は長く取る。
        ///   誤爆しても未 ack 分が再送されるだけなので安全側に倒れる。
        ///   <c>KeepAliveInterval</c> と併用して half-open を検出する。
        /// </summary>
        private const int SilenceWatchdogMs = 300000;

        /// <summary>ack をまとめて送るまでの猶予。累積 ack なので最大値の1回で足りる。</summary>
        private const int AckFlushMs = 20;

        private const int ReceiveBufferSize = 8192;

        /// <summary>受け取った生フレーム。パースは呼び出し側。</summary>
        public event Action<string> FrameReceived;

        public event Action Connected;
        public event Action Disconnected;
        public event Action<string> Log;
        public event Action<string> Warn;

        private readonly string _url;
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private ClientWebSocket _socket;
        private bool _closed;
        private int _attempt;
        private long _openedAtMs;
        private long _lastReceivedAtMs;

        private long? _pendingAckSeq;
        private string _pendingAckEpochId;
        private long _ackDeadlineMs;

        private readonly Random _random = new Random();

        public SpeechClient(string url)
        {
            _url = url;
        }

        /// <summary>
        /// <c>playerServerUrl</c> が空のときの接続先。
        ///
        /// ★ <c>host</c> をそのまま使えない。既定の <c>0.0.0.0</c> は
        ///   <b>bind アドレスであって接続先ではない</b>。
        /// </summary>
        public static string DeriveServerUrl(string host, int port)
        {
            var target = string.IsNullOrEmpty(host) || host == "0.0.0.0" || host == "::" ? "127.0.0.1" : host;
            // IPv6 リテラルは角括弧で囲む必要がある
            var authority = target.Contains(":") ? "[" + target + "]" : target;
            return "ws://" + authority + ":" + port;
        }

        public void Start()
        {
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            while (!_closed && !_cancellation.IsCancellationRequested)
            {
                var socket = new ClientWebSocket();
                // ★ half-open の検出はこれと SilenceWatchdogMs の2本立て。
                //   ClientWebSocket は ping を受けても通知しないので、送る側だけ設定できる
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                _socket = socket;

                try
                {
                    await socket.ConnectAsync(new Uri(_url), _cancellation.Token);
                }
                catch (Exception e)
                {
                    if (_closed) break;
                    // 起動直後にサーバーが居ないのは通常のこと。毎回スタックを出さない
                    Warn?.Invoke("接続エラー: " + e.Message);
                    socket.Dispose();
                    await BackoffAsync();
                    continue;
                }

                _openedAtMs = NowMs();
                _lastReceivedAtMs = _openedAtMs;
                Log?.Invoke("接続しました: " + _url);
                Connected?.Invoke();

                await ReceiveLoopAsync(socket);

                if (_socket == socket) _socket = null;
                socket.Dispose();
                if (_closed) break;

                // 安定して繋がっていられたなら、次の切断は「たまたま」として最短から試す
                if (_openedAtMs != 0 && NowMs() - _openedAtMs >= BackoffResetAfterMs) _attempt = 0;
                _openedAtMs = 0;

                Warn?.Invoke("切断されました。繋ぎ直します");
                Disconnected?.Invoke();
                await BackoffAsync();
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket)
        {
            var buffer = new byte[ReceiveBufferSize];
            var message = new MemoryStream();

            try
            {
                while (socket.State == WebSocketState.Open && !_cancellation.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellation.Token);
                    _lastReceivedAtMs = NowMs();

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var text = Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0);
                    FrameReceived?.Invoke(text);
                }
            }
            catch (OperationCanceledException)
            {
                // 終了処理
            }
            catch (Exception e)
            {
                if (!_closed) Warn?.Invoke("受信でエラー: " + e.Message);
            }
        }

        private async Task BackoffAsync()
        {
            if (_closed) return;
            var baseMs = Math.Min(BackoffMinMs * Math.Pow(2, _attempt), BackoffMaxMs);
            // ジッタを入れて、複数プロセスが同じ間隔で叩き続けるのを避ける
            var delay = (int)(baseMs / 2 + _random.NextDouble() * (baseMs / 2));
            _attempt++;
            try
            {
                await Task.Delay(delay, _cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // 終了処理
            }
        }

        /// <summary>
        /// 累積 ack。短時間に何度呼んでも、最大値が1回だけ飛ぶ。
        ///
        /// <paramref name="epochId"/> はサーバーが名乗っている採番の世代。<b>間引きの間に世代が
        /// 変わったら溜めていたぶんは捨てる</b> — 旧世代の ack を新しいサーバーへ打つと、
        /// <c>ackUpTo</c> がまだ喋っていない entry を消す。
        /// </summary>
        public void Ack(long seq, string epochId)
        {
            if (_pendingAckSeq == null || _pendingAckEpochId != epochId)
            {
                _pendingAckSeq = seq;
                _pendingAckEpochId = epochId;
                _ackDeadlineMs = NowMs() + AckFlushMs;
                return;
            }
            _pendingAckSeq = Math.Max(_pendingAckSeq.Value, seq);
        }

        /// <summary>
        /// まだ送っていない ack を捨てる。
        ///
        /// ★ 採番のやり直しを検出したら必ず呼ぶこと。間引きバッファに旧エポックの ack が
        ///   残っていると、<b>切断を挟まなくても</b>それが新しいサーバーへ飛び、
        ///   <c>ackUpTo</c> がまだ喋っていない entry を消す。
        /// </summary>
        public void DropPendingAck()
        {
            _pendingAckSeq = null;
            _pendingAckEpochId = null;
        }

        /// <summary>
        /// ドライバが毎フレーム呼ぶ。ack の間引き送出と、無受信 watchdog を進める。
        /// </summary>
        public void Tick()
        {
            var now = NowMs();

            if (_pendingAckSeq != null && now >= _ackDeadlineMs) FlushAck();

            var socket = _socket;
            if (socket != null && socket.State == WebSocketState.Open &&
                _lastReceivedAtMs != 0 && now - _lastReceivedAtMs > SilenceWatchdogMs)
            {
                Warn?.Invoke("サーバーから何も届かない状態が続いています。接続し直します");
                _lastReceivedAtMs = now;
                // Close ではなく Abort。half-open では close ハンドシェイクが返ってこない
                try { socket.Abort(); } catch (Exception) { /* 既に閉じている */ }
            }
        }

        private void FlushAck()
        {
            if (_pendingAckSeq == null) return;

            var socket = _socket;
            // ★ 送れると分かってから消費すること。先に消すと、状態機械が接続中のつもりで ack を
            //   出した直後にソケットが閉じていたケースで、ack が両側から消えて復旧手段が無くなる
            if (socket == null || socket.State != WebSocketState.Open) return;

            var seq = _pendingAckSeq.Value;
            var epochId = _pendingAckEpochId;
            _pendingAckSeq = null;
            _pendingAckEpochId = null;

            // ★ epoch を必ず載せる。**null にしないこと** — 契約上は「省略」と同じ扱いだが、
            //   意図せず「世代を名乗らない ack」になる
            var json = "{\"type\":\"spoken\",\"seq\":" + seq + ",\"epoch\":" + Quote(epochId) + "}";
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                _ = socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cancellation.Token);
            }
            catch (Exception e)
            {
                Warn?.Invoke("ack の送信に失敗しました: " + e.Message);
            }
        }

        /// <summary>再接続をやめて閉じる。</summary>
        public async Task CloseAsync()
        {
            _closed = true;
            // 喋り終えた直後に終了しても、間引き中の ack は投げてから閉じる。
            // 落とすと次回起動でその文がもう一度鳴る
            FlushAck();

            var socket = _socket;
            _socket = null;
            _cancellation.Cancel();

            if (socket == null) return;
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
                    }
                }
            }
            catch (Exception)
            {
                // 相手が応答しないときのため。close ハンドシェイクを待ち続けない
            }
            finally
            {
                try { socket.Abort(); } catch (Exception) { /* 既に閉じている */ }
                socket.Dispose();
            }
        }

        /// <summary><c>epoch</c> の charset は限定されているが、JSON として正しく出す。</summary>
        private static string Quote(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in value)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static long NowMs()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
