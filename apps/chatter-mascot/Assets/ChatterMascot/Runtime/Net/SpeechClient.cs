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

        /// <summary>
        /// ack の送信に失敗したあと、次に試すまでの間隔。
        ///
        /// ★ 失敗しても <c>_pendingAckSeq</c> は消さない（→ <see cref="FlushAckAsync"/>）ので、
        ///   これが無いと <c>Tick()</c> のたびに叩き直すことになる。
        /// </summary>
        private const int AckRetryMs = 1000;

        /// <summary>
        /// 終了時、最後の ack を投げ切るのに使う予算。
        ///
        /// ★ 参照実装（<c>core/src/player/index.ts</c>）の <c>step()</c> が
        ///   2.5秒で切っているのと同じ理由。応答しない相手を掴むと終了できなくなる。
        /// </summary>
        private const int CloseAckBudgetMs = 2000;

        private const int ReceiveBufferSize = 8192;

        /// <summary>
        /// 1フレームの上限。超えたら接続を捨てて繋ぎ直す。
        ///
        /// ★ <b><c>EndOfMessage</c> が来ない限りバッファは伸び続ける。</b> 契約上のフレームは
        ///   1メッセージぶんのテキストなので、壊れたプロデューサーか間に何か挟まっているだけで
        ///   OOM に行き着く。上限が要るのはこの1点で、「再接続のたびに積まれる」からではない
        ///   （<c>message</c> は受信ループのローカルで、抜ければ参照が切れて GC される）。
        /// </summary>
        private const int MaxFrameBytes = 4 * 1024 * 1024;

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

        /// <summary>
        /// 送信中フラグ。<c>ClientWebSocket.SendAsync</c> は同時に2本走らせられない
        /// （<c>InvalidOperationException</c>）。
        ///
        /// ★ <c>SemaphoreSlim</c> は要らない。Unity の <c>SynchronizationContext</c> により
        ///   継続はメインスレッドで走るので、<see cref="Tick"/> と await の再開が
        ///   同時に動くことはない。
        /// </summary>
        private bool _sending;

        /// <summary>接続ごとに1回で足りる警告のラッチ。壊れた相手だとログが洪水になる。</summary>
        private bool _warnedAckFailure;

        /// <summary>
        /// <b>閉じる前に投げ切るものが残っているか。</b>
        ///
        /// ★ <b>終了を保留するかどうかの判断にだけ使う</b>
        ///   （<see cref="ChatterMascot.ShutdownPolicy.ShouldDefer"/>）。未 ack が無いのに
        ///   毎回保留していたのが
        ///   <a href="https://github.com/schwarz9791/chatter-agent/issues/68">#68</a>
        ///   （Dock からの「終了」を2回選ぶ必要がある）の入口だった。
        ///
        /// ★ <b><c>_sending</c> も見ること。</b> <see cref="FlushAckAsync"/> は成功するまで
        ///   <c>_pendingAckSeq</c> を消さないが、<b>送信が飛行中の一瞬だけ</b>
        ///   <c>_pendingAckSeq</c> が null になる窓がある（成功の直後・<c>_sending</c> が
        ///   戻る直前）。そこで「投げ切るものは無い」と読むと、
        ///   <c>CloseAsync</c> を待たずに <c>_cancellation.Cancel()</c> まで進んで
        ///   <b>飛行中の ack を自分で切る</b>。
        /// </summary>
        public bool HasPendingWork => _pendingAckSeq != null || _sending;

        private readonly Random _random = new Random();

        public SpeechClient(string url)
        {
            _url = url;
        }

        public void Start()
        {
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            // ★ **最後の受け皿。復旧のためではなく、可視化のために置く。**
            //   RunAsync は `_ = RunAsync()` で起動しているので、ここまで例外が上がると
            //   その Task が fault したまま**誰にも観測されずに捨てられる**。
            //   ログが1行も出ないまま再接続ループだけが消え、セッションが終わるまで無音になる
            //   ——「サーバーが何も言っていない」と区別がつかない、いちばん困る壊れ方。
            //   個々のコールバックは SafeInvoke で握ってあるので、ここに来るのは想定外だけ。
            try
            {
                await ConnectLoopAsync();
            }
            catch (Exception e)
            {
                Warn?.Invoke("再接続ループが止まりました（アプリの再起動が要ります）: " + e);
            }
        }

        private async Task ConnectLoopAsync()
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
                _warnedAckFailure = false;
                Log?.Invoke("接続しました: " + _url);
                SafeInvoke(Connected, "Connected");

                await ReceiveLoopAsync(socket);

                // ★ **Dispose の前に読むこと。** 閉じた後は CloseStatus が取れない
                var closeDetail = DescribeClose(socket);

                if (_socket == socket) _socket = null;
                socket.Dispose();
                if (_closed) break;

                // 安定して繋がっていられたなら、次の切断は「たまたま」として最短から試す
                if (_openedAtMs != 0 && NowMs() - _openedAtMs >= BackoffResetAfterMs) _attempt = 0;
                _openedAtMs = 0;

                Warn?.Invoke("切断されました" + closeDetail + "。繋ぎ直します");
                SafeInvoke(Disconnected, "Disconnected");
                await BackoffAsync();
            }
        }

        /// <summary>
        /// 購読者の例外を接続の外へ出さない。
        ///
        /// ★ <b><see cref="SpeechClient"/> は購読者が何をするか知らない。</b>
        ///   <c>MascotRunner</c> は3つのイベントすべてを <c>PlaybackQueue.Reduce</c> →
        ///   コマンド実行へ繋いでいるので、そこの1つの例外が<b>受信ループや再接続ループを
        ///   道連れにする</b>形にしてはいけない。
        ///
        /// ★ <b>握るのは「例外が出ること」への対処ではなく、「出たことが誰にも分からない」
        ///   ことへの対処。</b> 必ず <see cref="Warn"/> に出す。
        /// </summary>
        private void SafeInvoke(Action handler, string what)
        {
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception e)
            {
                Warn?.Invoke(what + " の購読者が例外を投げました（無視して続けます）: " + e.Message);
            }
        }

        /// <summary>
        /// 切断の理由を人が読める形にする。
        ///
        /// ★ <b>コードを落とさないこと。</b> サーバーの切断理由は2つあり
        /// （<c>wsServer.ts</c>）、区別できないと無音の原因に辿り着けない:
        ///
        ///   - <c>close(1013, "too slow")</c> — バックプレッシャ。<b>こちらが遅い</b>
        ///   - <c>terminate()</c> — ping に pong が返らなかった。<b>こちらが応答できていない</b>
        ///
        /// どちらも「クライアント側が詰まっている」を意味するので、出さないと
        /// 「たまたま切れた」と読んでしまう。参照実装（<c>core/src/player/client.ts</c>）は
        /// <c>code=</c> を出している。
        ///
        /// ★ <b>close フレームが来ないのも情報。</b> <c>terminate()</c> は TCP をいきなり
        ///   切るので <c>CloseStatus</c> が null になる。こちらの無受信 watchdog が
        ///   <c>Abort()</c> したときも同じなので、直前の警告と併せて読む。
        /// </summary>
        private static string DescribeClose(ClientWebSocket socket)
        {
            WebSocketCloseStatus? status;
            string description;
            try
            {
                status = socket.CloseStatus;
                description = socket.CloseStatusDescription;
            }
            catch (Exception)
            {
                // 既に破棄されている
                return string.Empty;
            }

            if (status == null) return "（close フレーム無し。サーバーの terminate か、こちらの watchdog）";

            // ★ 1013（try again later）は WebSocketCloseStatus に定義が無いので数値で出す
            var code = ((int)status.Value).ToString();
            return string.IsNullOrEmpty(description) ? " (code=" + code + ")" : " (code=" + code + " " + description + ")";
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket)
        {
            var buffer = new byte[ReceiveBufferSize];

            try
            {
                using (var message = new MemoryStream())
                {
                    while (socket.State == WebSocketState.Open && !_cancellation.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellation.Token);
                        _lastReceivedAtMs = NowMs();

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        if (message.Length + result.Count > MaxFrameBytes)
                        {
                            Warn?.Invoke($"1フレームが {MaxFrameBytes / (1024 * 1024)}MB を超えました。接続し直します");
                            break;
                        }

                        message.Write(buffer, 0, result.Count);
                        if (!result.EndOfMessage) continue;

                        var text = Encoding.UTF8.GetString(message.ToArray());
                        message.SetLength(0);

                        // ★ **1フレームぶんだけ握って、次のフレームへ進む。接続は切らない。**
                        //   ここを握らないと、パーサの1つの例外（例: seq が long を超える）で
                        //   受信ループが終わり、繋ぎ直した先でサーバーが**同じ未 ack のフレームを
                        //   再送する**ので、また落ちる——直せないループになる
                        try
                        {
                            FrameReceived?.Invoke(text);
                        }
                        catch (Exception e)
                        {
                            Warn?.Invoke("フレームの処理で例外が出ました（このフレームは捨てます）: " + e.Message);
                        }
                    }
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

            // FlushAckAsync は中で全部握るので、fire-and-forget でも fault が漏れない
            if (_pendingAckSeq != null && now >= _ackDeadlineMs) _ = FlushAckAsync(_cancellation.Token);

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

        /// <summary>
        /// 溜めていた ack を送る。
        ///
        /// ★ <b>「消してから送る」ではなく「送れてから消す」。</b> 先に消すと、
        ///   送信が失敗したときに ack が<b>こちら側からも状態機械側からも消えて</b>
        ///   復旧手段が無くなる。復旧するのはサーバーが同じ entry を再送して
        ///   重複排除の枝が ack を再発行したときだけで、偶然に頼ることになる。
        ///
        /// ★ <b><c>await</c> すること。</b> <c>_ = SendAsync(...)</c> だと、送信中の例外
        ///   （送信の重なり、<c>State</c> 検査の直後にソケットが落ちた、half-open の書き込み
        ///   エラー）は<b>返り値の <c>Task</c> に載る</b>ので同期的には投げられず、
        ///   <c>catch</c> がほとんど発火しない。
        ///
        /// ★ <b>失敗しても戻す処理は要らない。</b> 消していないので、次の <see cref="Tick"/> が
        ///   そのまま再送する。「await の間に <c>DropPendingAck()</c> が走った」
        ///   「もっと新しい seq が積まれた」「世代が変わった」を見分ける復元処理は、
        ///   どれか1つ落とすと<b>まだ喋っていない entry を消す ack</b> が飛ぶ。
        ///   消さなければその分岐が存在しない。
        /// </summary>
        /// <param name="token">
        /// ★ <b>終了時は <c>_cancellation.Token</c> を渡さないこと。</b>
        ///   <see cref="CloseAsync"/> が直後に <c>Cancel()</c> するので、
        ///   たった今投げた送信を自分で中断することになる。
        /// </param>
        private async Task FlushAckAsync(CancellationToken token)
        {
            if (_sending || _pendingAckSeq == null) return;

            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open) return;

            var seq = _pendingAckSeq.Value;
            var epochId = _pendingAckEpochId;
            var bytes = Encoding.UTF8.GetBytes(BuildAck(seq, epochId));

            _sending = true;
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);

                // ★ 送れてから消す。await の間に積まれたぶん・世代が変わったぶんは残す
                //   （次の Tick が送る）。DropPendingAck が走っていたら、消す対象がもう無い
                if (_pendingAckSeq == seq && _pendingAckEpochId == epochId)
                {
                    _pendingAckSeq = null;
                    _pendingAckEpochId = null;
                }
            }
            catch (Exception e)
            {
                if (!_warnedAckFailure)
                {
                    _warnedAckFailure = true;
                    Warn?.Invoke("ack の送信に失敗しました（次の機会に送り直します）: " + e.Message);
                }
                _ackDeadlineMs = NowMs() + AckRetryMs;
            }
            finally
            {
                _sending = false;
            }
        }

        /// <summary>
        /// ★ <c>epoch</c> を必ず載せる。<b><c>null</c> にしないこと</b> —— 契約上は「省略」と
        ///   同じ扱いだが、意図せず「世代を名乗らない ack」になる。
        /// </summary>
        private static string BuildAck(long seq, string epochId)
        {
            return "{\"type\":\"spoken\",\"seq\":" + seq + ",\"epoch\":" + Quote(epochId) + "}";
        }

        /// <summary>再接続をやめて閉じる。</summary>
        public async Task CloseAsync()
        {
            _closed = true;

            // 喋り終えた直後に終了しても、間引き中の ack は投げてから閉じる。
            // 落とすと次回起動でその文がもう一度鳴る。
            //
            // ★ **Cancel() より前に、_cancellation.Token とは別のトークンで待つこと。**
            //   同じトークンを渡すと、たった今投げた送信を自分で中断する。
            var pendingAtClose = _pendingAckSeq;
            using (var budget = new CancellationTokenSource(CloseAckBudgetMs))
            {
                try
                {
                    await FlushAckAsync(budget.Token);
                }
                catch (Exception)
                {
                    // 予算切れ。次回起動でその文がもう一度鳴るだけなので、ここで粘らない
                }
            }

            // ★ **終了処理が何をしたかを残すこと。** 保留中の ack があったかどうかは
            //   再生終了から数十 ms の窓でしか変わらないので、**ログが無いと
            //   「終了処理が働いたのか、そもそも出番が無かったのか」を後から区別できない**
            //   （実機確認でここに詰まった）。送れなかった側は次回起動での二重発話に
            //   直結するので、必ず出す。
            if (pendingAtClose != null)
            {
                if (_pendingAckSeq == null)
                {
                    Log?.Invoke($"終了時に保留していた ack を送りました (seq={pendingAtClose})");
                }
                else
                {
                    Warn?.Invoke($"終了時に ack を送れませんでした (seq={pendingAtClose})。" +
                                 "次回起動でその文がもう一度鳴ります");
                }
            }

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
