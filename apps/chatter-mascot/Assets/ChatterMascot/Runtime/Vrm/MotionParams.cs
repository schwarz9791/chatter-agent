namespace ChatterMascot.Vrm
{
    /// <summary>
    /// #70 のモーション再生パラメータ。<c>IdleParams</c> / <c>FaceParams</c> と同じ流儀の
    /// <c>readonly struct</c>。テストが独自の値（短いクールダウンなど）を注入できるよう、
    /// 全フィールドを取るコンストラクタを持つ。
    /// </summary>
    public readonly struct MotionParams
    {
        /// <summary>
        /// ワンショットモーション ⇔ 待機のクロスフェードにかける秒数。
        /// <c>CrossFade.Progress</c> の <c>durationSeconds</c> に渡す。
        /// </summary>
        public readonly float FadeSeconds;

        /// <summary>
        /// 感情モーションが終わってから、次の感情モーションを許すまでの最短間隔（秒）。
        ///
        /// ★ cc-mascot と違い、<b>再生中の感情モーションには割り込まない</b>（最後まで見せる）
        ///   ぶん、終了後のクールダウンで連発を抑える（ユーザーと決めたこと。2026-09-04）。
        ///   起点は <c>EmotionMotionTrigger.NotifyEnded</c>。
        /// </summary>
        public readonly double CooldownSeconds;

        /// <summary>待機の小ネタ（<c>idle/</c>）が発火するまでの間隔の下限（秒）。</summary>
        public readonly double AccentMinSeconds;

        /// <summary>待機の小ネタが発火するまでの間隔の上限（秒）。<c>IdleAccentTimer</c> がこの間で乱数を引く。</summary>
        public readonly double AccentMaxSeconds;

        public MotionParams(
            float fadeSeconds,
            double cooldownSeconds,
            double accentMinSeconds,
            double accentMaxSeconds)
        {
            FadeSeconds = fadeSeconds;
            CooldownSeconds = cooldownSeconds;
            AccentMinSeconds = accentMinSeconds;
            AccentMaxSeconds = accentMaxSeconds;
        }

        public static MotionParams Default => new MotionParams(
            fadeSeconds: 0.5f,
            cooldownSeconds: 5.0,
            accentMinSeconds: 30.0,
            accentMaxSeconds: 60.0);
    }
}
