namespace ChatterMascot.Window
{
    /// <summary>保存を試みるかどうか。</summary>
    public enum SaveAction
    {
        /// <summary>書く必要が無い（同じ / 無効 / 諦めた）。保留は落としてよい。</summary>
        Skip,

        /// <summary>書く。</summary>
        Write,

        /// <summary>直前に失敗した。<b>保留は落とさず</b>、次の機会まで待つ。</summary>
        WaitForRetry,
    }

    /// <summary>
    /// 保存を試みるかどうかの判断だけを持つ。<b>純粋関数。</b>
    ///
    /// ★ <b>なぜ切り出すか。</b> この判断は <c>UniWindowController</c> に依存する層にあり、
    /// そのままでは EditMode から1行も固定できない（<c>ShutdownPolicy</c> /
    /// <c>AudioIdleGate</c> と同じ理由）。固定したいのは、<b>失敗の仕方が静かで
    /// 気づけない</b>2つの規則:
    ///
    /// <list type="bullet">
    ///   <item><b>矩形が同じでも構成の指紋が違えば書く</b>（→ <see cref="WindowState.SameAs"/>）</item>
    ///   <item><b>失敗しても保留を落とさない。ただし必ず待たせ、いつかは諦める</b></item>
    /// </list>
    /// </summary>
    public static class SavePolicy
    {
        /// <summary>
        /// <paramref name="consecutiveFailures"/> は<b>連続で失敗した回数</b>、
        /// <paramref name="retryAtSeconds"/> は<b>次に試してよい時刻</b>。
        ///
        /// ★ <b>失敗を数えて諦めること。</b> 保留を落とさないだけだと、書けない状態が続く限り
        ///   <b>毎フレーム書き込みを試して警告が洪水になる</b>（保存先が読み取り専用、
        ///   ディスク満杯など）。待たせるだけでも間隔は空くが<b>永遠に鳴り続ける</b>ので、
        ///   上限も要る。
        /// </summary>
        public static SaveAction Decide(
            WindowState candidate,
            WindowState lastPersisted,
            int consecutiveFailures,
            int maxFailures,
            double nowSeconds,
            double retryAtSeconds)
        {
            // 書くものが無い。保留を持ち越す意味も無い
            if (!candidate.Rect.IsValid) return SaveAction.Skip;

            // ★ 諦めは「同じか」より先に見る。書けない原因は内容ではないので、
            //   内容が変わったからといって蒸し返すと警告が復活する
            if (maxFailures > 0 && consecutiveFailures >= maxFailures) return SaveAction.Skip;

            if (candidate.SameAs(lastPersisted)) return SaveAction.Skip;

            if (consecutiveFailures > 0 && nowSeconds < retryAtSeconds) return SaveAction.WaitForRetry;

            return SaveAction.Write;
        }
    }
}
