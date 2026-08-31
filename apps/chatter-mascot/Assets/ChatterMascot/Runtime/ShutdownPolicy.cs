namespace ChatterMascot
{
    /// <summary>
    /// 終了要求をどう捌くかの判断だけを持つ。<b>純粋関数。</b>
    ///
    /// ★ <b>なぜ切り出すか。</b> 終了経路は <c>Application.wantsToQuit</c> →
    /// 非同期の後始末 → <c>Application.Quit()</c> と続き、<b>Editor の Play Mode では
    /// 戻り値が無視される</b>（Unity のドキュメントが明記）ので<b>ビルドした <c>.app</c> でしか
    /// 通らない</b>。判断まで <c>MonoBehaviour</c> に埋めると EditMode で1行も固定できなくなる
    /// （<c>MascotRunner.IsParked</c> / <c>ReadFace</c> を <c>public static</c> に
    /// してあるのと同じ理由）。
    ///
    /// ★ <b>保留そのものをやめないこと。</b> <c>OnDestroy</c> から
    /// <c>_ = client.CloseAsync()</c> を投げても await の継続が走る前にプロセスが消え、
    /// 喋り終えた ack が落ちて<b>次回起動でその文がもう一度鳴る</b>
    /// （<c>docs/mascot.md</c> に実測付き）。ここで変えるのは
    /// 「<b>いつも</b>保留する」を「<b>投げるものがあるときだけ</b>保留する」にすることだけ。
    /// </summary>
    public static class ShutdownPolicy
    {
        /// <summary>
        /// <c>Application.wantsToQuit</c> で終了を<b>1回だけ</b>保留するか。
        ///
        /// <list type="bullet">
        ///   <item><paramref name="alreadyRequested"/>（2周目）なら通す。
        ///         ★ ここで保留し続けると<b>アプリが終了しなくなる</b></item>
        ///   <item>投げ切るものが無ければ通す。★ <b>これが
        ///         <a href="https://github.com/schwarz9791/chatter-agent/issues/68">#68</a>
        ///         の本題</b> —— 未 ack が無いのに毎回保留していたので、
        ///         <c>Application.Quit()</c> が効かない環境では「終了」を2回選ぶことになる</item>
        ///   <item>それ以外は保留する</item>
        /// </list>
        /// </summary>
        public static bool ShouldDefer(bool hasPendingWork, bool alreadyRequested)
        {
            if (alreadyRequested) return false;
            return hasPendingWork;
        }

        /// <summary>
        /// <c>Application.Quit()</c> を呼び直すか。
        ///
        /// ★ <b>「効かないこと」を観測できるようにするための仕組み。</b> 仮説
        /// （<c>wantsToQuit</c> で <c>false</c> を返した後の <c>Quit()</c> が macOS で
        /// 無視されている）が当たっているかは、<b>再試行のログが出るかどうかでしか分からない</b>。
        /// 当たっていれば手当てはネイティブ側
        /// （<a href="https://github.com/schwarz9791/chatter-agent/issues/75">#75</a> の
        /// <c>replyToApplicationShouldTerminate:</c>）になるが、
        /// <b>原因が確定する前にネイティブを足すと、効いた理由が分からなくなる</b>。
        ///
        /// <paramref name="attempts"/> は<b>これまでに呼んだ回数</b>（初回の呼び出し後は 1）。
        /// <paramref name="maxAttempts"/> か <paramref name="intervalSeconds"/> が
        /// 0 以下なら再試行しない（キルスイッチ）。
        ///
        /// ★ <b>時計が巻き戻っても撃たない。</b> <c>Time.realtimeSinceStartup</c> は
        /// 巻き戻らない前提だが、差分でしか見ない書き方にしておく
        /// （<c>AudioIdleGate</c> と同じ規律）。
        /// </summary>
        public static bool ShouldRetryQuit(
            double nowSeconds,
            double lastAttemptSeconds,
            int attempts,
            int maxAttempts,
            double intervalSeconds)
        {
            if (maxAttempts <= 0 || intervalSeconds <= 0) return false;
            if (attempts <= 0) return false;
            if (attempts >= maxAttempts) return false;

            var elapsed = nowSeconds - lastAttemptSeconds;
            if (elapsed < 0) return false;
            return elapsed >= intervalSeconds;
        }
    }
}
