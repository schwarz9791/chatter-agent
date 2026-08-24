namespace ChatterMascot.Playback
{
    /// <summary>
    /// 発話キューの判断に効く設定。<c>core/src/player/playbackQueue.ts</c> の
    /// <c>PlaybackOptions</c> の移植。
    /// </summary>
    public sealed class PlaybackOptions
    {
        /// <summary>再生中の1件を含めて、いくつ先まで音声を取りに行くか。0 なら完全直列。</summary>
        public int Lookahead = 3;

        /// <summary>これより古い発話は音を出さずに飛ばす。0 なら無効。</summary>
        public long MaxAgeMs = 0;

        /// <summary>
        /// 取得を試みる上限回数。2 = 初回 + 1リトライ。
        /// <b>503 はこれを消費しない</b>（→ <see cref="PlaybackQueue"/> の <c>AudioUnavailable</c>）。
        /// </summary>
        public int SynthesisAttempts = 2;

        /// <summary>
        /// 503（あとで取りに来い）を受けてから取り直すまでの<b>最初の</b>間隔。
        /// 連続して 503 が返る間は <see cref="AudioRetryMaxMs"/> まで倍々にする。
        /// </summary>
        public long AudioRetryMs = 1000;

        /// <summary>
        /// 503 が続いたときの取り直し間隔の上限。
        ///
        /// ★ 固定のままだと、エンジンが落ちている間ずっと<b>先読み窓ぶん × 間隔</b>の
        ///   リクエストがサーバーへ飛び続ける。503 では試行回数を消費しない（＝諦めない）設計なので、
        ///   止めるのは「捨てること」ではなく<b>バックオフ</b>の役目。
        /// </summary>
        public long AudioRetryMaxMs = 30000;

        /// <summary>
        /// 音声を用意できない状態がこの件数続いたら警告する。0 なら無効。
        ///
        /// ★ <b>無音の原因を手元に残す唯一の窓。</b> 合成がサーバーへ移ったので、
        ///   エンジンの不在も <c>ttsSpeakerId</c> の間違いも、クライアントからは
        ///   「503 / 404 が続く」としてしか見えない。
        /// </summary>
        public int UnavailableWarnAfter = 5;

        /// <summary>
        /// 用意できない状態が続くとき、警告を出し直す間隔。0 なら1回きり。
        ///
        /// ★ <b>bool のラッチにしないこと。</b> プロセス寿命で1回だけにすると、長く走らせたときに
        ///   「エンジン停止 → 復旧 → 再停止」を見ても最初の1回しか出さない。
        /// </summary>
        public long UnavailableWarnRepeatMs = 60000;

        /// <summary>消費済みキーの保持数。</summary>
        public int SeenCapacity = 512;

        /// <summary>head が動かないまま この時間 が過ぎたら警告する。0 なら無効。</summary>
        public long StallWarnMs = 120000;
    }
}
