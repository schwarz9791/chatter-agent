using ChatterMascot.Protocol;

namespace ChatterMascot.Playback
{
    public enum PlaybackEventKind
    {
        /// <summary>フレームを受信した</summary>
        Received,

        /// <summary>音声を取得できた</summary>
        AudioReady,

        /// <summary>503。試行回数を消費せず、バックオフして取り直す</summary>
        AudioUnavailable,

        /// <summary>404 / 音声そのものが無い。諦めて（＝長さ0の再生として）ack する</summary>
        AudioGone,

        /// <summary>転送の失敗。試行回数を消費する</summary>
        AudioFailed,

        Played,
        PlaybackFailed,
        Connected,
        Disconnected,

        /// <summary>時間で進む判断（stale / stall watchdog / 503 のバックオフ）のためだけに入れる</summary>
        Tick,
    }

    /// <summary>
    /// 状態機械への入力。
    ///
    /// ★ <b>非同期の結果を戻すイベントには必ず <see cref="Epoch"/> を載せること。</b>
    ///   <c>Seq</c> は<b>エポックを跨いで一意ではない</b>。採番がやり直された直後に古い取得が
    ///   返ってくると、<c>Seq</c> だけで突き合わせる実装は<b>同じ seq の新しい item に
    ///   別の文の音声を入れる</b>。「こんにちは」を鳴らしながら「さようなら」を ack する、
    ///   という壊れ方をする。
    /// </summary>
    public readonly struct PlaybackEvent
    {
        public readonly PlaybackEventKind Kind;

        /// <summary><see cref="PlaybackEventKind.Received"/> のときだけ。</summary>
        public readonly SpeechFrame Record;

        /// <summary><b>プロセス内の</b>エポック通し番号（サーバー由来の文字列ではない）。</summary>
        public readonly int Epoch;

        public readonly long Seq;

        /// <summary>
        /// 取得できた音声。状態機械は<b>中身を知らない</b>（Unity では <c>AudioClip</c>、
        /// テストではダミー）。純粋さを保つために不透明なハンドルとして扱う。
        /// </summary>
        public readonly object Audio;

        public readonly string Reason;

        private PlaybackEvent(PlaybackEventKind kind, SpeechFrame record, int epoch, long seq, object audio, string reason)
        {
            Kind = kind;
            Record = record;
            Epoch = epoch;
            Seq = seq;
            Audio = audio;
            Reason = reason;
        }

        public static PlaybackEvent Received(SpeechFrame record) =>
            new PlaybackEvent(PlaybackEventKind.Received, record, 0, 0, null, null);

        public static PlaybackEvent AudioReady(int epoch, long seq, object audio) =>
            new PlaybackEvent(PlaybackEventKind.AudioReady, null, epoch, seq, audio, null);

        public static PlaybackEvent AudioUnavailable(int epoch, long seq, string reason) =>
            new PlaybackEvent(PlaybackEventKind.AudioUnavailable, null, epoch, seq, null, reason);

        public static PlaybackEvent AudioGone(int epoch, long seq, string reason) =>
            new PlaybackEvent(PlaybackEventKind.AudioGone, null, epoch, seq, null, reason);

        public static PlaybackEvent AudioFailed(int epoch, long seq, string reason) =>
            new PlaybackEvent(PlaybackEventKind.AudioFailed, null, epoch, seq, null, reason);

        public static PlaybackEvent Played(int epoch, long seq) =>
            new PlaybackEvent(PlaybackEventKind.Played, null, epoch, seq, null, null);

        public static PlaybackEvent PlaybackFailed(int epoch, long seq, string reason) =>
            new PlaybackEvent(PlaybackEventKind.PlaybackFailed, null, epoch, seq, null, reason);

        public static PlaybackEvent Connected() =>
            new PlaybackEvent(PlaybackEventKind.Connected, null, 0, 0, null, null);

        public static PlaybackEvent Disconnected() =>
            new PlaybackEvent(PlaybackEventKind.Disconnected, null, 0, 0, null, null);

        public static PlaybackEvent Tick() =>
            new PlaybackEvent(PlaybackEventKind.Tick, null, 0, 0, null, null);
    }

    public enum PlaybackCommandKind
    {
        FetchAudio,
        Play,

        /// <summary>
        /// 累積 ack（「seq までは片付いた」）。
        ///
        /// ★ 送出の間引きは<b>ドライバの責務</b>。接続直後の追いつきで消費済みの entry が
        ///   まとめて再送されると、ここからはその件数ぶんの ack コマンドが出る。累積なので
        ///   最大値の1回で足りる。
        /// </summary>
        Ack,

        /// <summary>
        /// ドライバが溜めている未送出の ack を捨てる。
        ///
        /// ★ エポックが変わった瞬間に必ず出すこと。ドライバ側の間引きバッファに旧エポックの
        ///   ack が残っていると、<b>切断を挟まなくても</b>それが新しいサーバーに飛ぶ。
        ///   サーバーの <c>ackUpTo</c> はファイル名で範囲削除するので、まだ喋っていない entry が消える。
        /// </summary>
        DropPendingAck,

        /// <summary>使い終わった（あるいは捨てた）音声を解放する</summary>
        DiscardAudio,

        Log,
        Warn,
    }

    public readonly struct PlaybackCommand
    {
        public readonly PlaybackCommandKind Kind;
        public readonly int Epoch;
        public readonly long Seq;

        /// <summary><see cref="PlaybackCommandKind.FetchAudio"/> のときの相対パス。</summary>
        public readonly string Path;

        /// <summary><see cref="PlaybackCommandKind.Play"/> / <see cref="PlaybackCommandKind.DiscardAudio"/> の音声ハンドル。</summary>
        public readonly object Audio;

        /// <summary><see cref="PlaybackCommandKind.Ack"/> のとき、サーバーが名乗っている世代。</summary>
        public readonly string EpochId;

        public readonly string Message;

        private PlaybackCommand(PlaybackCommandKind kind, int epoch, long seq, string path, object audio, string epochId, string message)
        {
            Kind = kind;
            Epoch = epoch;
            Seq = seq;
            Path = path;
            Audio = audio;
            EpochId = epochId;
            Message = message;
        }

        public static PlaybackCommand FetchAudio(int epoch, long seq, string path) =>
            new PlaybackCommand(PlaybackCommandKind.FetchAudio, epoch, seq, path, null, null, null);

        public static PlaybackCommand Play(int epoch, long seq, object audio) =>
            new PlaybackCommand(PlaybackCommandKind.Play, epoch, seq, null, audio, null, null);

        public static PlaybackCommand Ack(long seq, string epochId) =>
            new PlaybackCommand(PlaybackCommandKind.Ack, 0, seq, null, null, epochId, null);

        public static PlaybackCommand DropPendingAck() =>
            new PlaybackCommand(PlaybackCommandKind.DropPendingAck, 0, 0, null, null, null, null);

        public static PlaybackCommand DiscardAudio(int epoch, long seq, object audio) =>
            new PlaybackCommand(PlaybackCommandKind.DiscardAudio, epoch, seq, null, audio, null, null);

        public static PlaybackCommand Log(string message) =>
            new PlaybackCommand(PlaybackCommandKind.Log, 0, 0, null, null, null, message);

        public static PlaybackCommand Warn(string message) =>
            new PlaybackCommand(PlaybackCommandKind.Warn, 0, 0, null, null, null, message);
    }
}
