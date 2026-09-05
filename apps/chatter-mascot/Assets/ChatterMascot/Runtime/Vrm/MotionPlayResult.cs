namespace ChatterMascot.Vrm
{
    /// <summary>
    /// <c>VrmMotionPlayer.Play</c>（および <c>VrmCharacter.PreviewMotion</c> /
    /// <c>ISettingsHost.PlayMotion</c>）が返す、再生を開始できたか・できなかったならその理由。
    ///
    /// ★ <c>VrmMotionPlayer.Play</c> の拒否条件と 1 対 1 で対応する（#70 レビュー #5）。
    ///   以前は全部 <c>bool</c> の <c>false</c> にまとめていたため、設定パネルの「再生」ボタンは
    ///   拒否理由が違っても常に同じ「再生中です」を出していた。文言は
    ///   <c>SettingsSchema.MotionPlayNotice</c>（純粋関数）がこの値から決める。
    /// </summary>
    public enum MotionPlayResult
    {
        /// <summary>再生を開始した。</summary>
        Started,

        /// <summary><c>VrmMotionPlayer</c> が既に破棄されている。</summary>
        Disposed,

        /// <summary>待機モーション（<c>VrmIdleAnimation</c>）がまだ読み込めていない。</summary>
        IdleNotLoaded,

        /// <summary>設定「待機モーション」が OFF になっている。</summary>
        IdleDisabled,

        /// <summary>指定したクリップが <c>null</c>、または読み込みが終わっていない（壊れた <c>.vrma</c> を含む）。</summary>
        NotLoaded,

        /// <summary>割り込めない何かが既に再生中（感情モーション同士、または小ネタ中の小ネタ）。</summary>
        Busy,
    }
}
