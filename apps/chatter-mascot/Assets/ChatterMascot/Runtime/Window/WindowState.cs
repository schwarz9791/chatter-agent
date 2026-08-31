namespace ChatterMascot.Window
{
    /// <summary>
    /// 永続化する値。<b>ポイントの矩形と、そのときのディスプレイ構成の指紋だけ</b>。
    ///
    /// ★ <b>Unity の永続化（<c>Screenmanager *</c>）に乗せない。</b> あちらは
    ///   <b>バッキング px</b> なので、Retina で終了して 1x で開くと窓が倍になる（実測済み）。
    ///   さらに <c>~/Library/Preferences/tech.sukima.chatter-mascot.plist</c> に書かれるため、
    ///   <b>実機確認の1行目 <c>defaults delete tech.sukima.chatter-mascot</c>（焼き付き消し）が
    ///   自前の永続化ごと消す</b>。<b>Unity が px を焼く場所と、我々が pt を書く場所を
    ///   物理的に分ける。</b>
    /// </summary>
    public readonly struct WindowState
    {
        public readonly PointRect Rect;

        /// <summary>
        /// 保存したときのディスプレイ構成（<see cref="DisplayLayout.Signature"/>）。
        /// <b>一致するかどうかだけを見る。</b>
        /// </summary>
        public readonly string DisplaySignature;

        public WindowState(PointRect rect, string displaySignature)
        {
            Rect = rect;
            DisplaySignature = displaySignature ?? string.Empty;
        }

        /// <summary>保存が無い（または読めなかった）状態。<c>Rect.IsValid</c> が false になる。</summary>
        public static WindowState None => default;

        /// <summary>
        /// 書き直す必要が無いほど同じか。
        ///
        /// ★ <b>矩形だけで比べないこと。</b> 窓が動かないまま構成だけ変わることがある
        ///   （マスコットを置いていない側のディスプレイを抜き差しする、など）。矩形だけで短絡すると
        ///   <b>古い構成の指紋が残り続け</b>、次の起動で「構成が変わった」と読まれて
        ///   <b>ユーザーが端に寄せた窓が押し戻される</b>。しかも理由はログに出ない。
        /// </summary>
        public bool SameAs(WindowState other) =>
            string.Equals(DisplaySignature, other.DisplaySignature, System.StringComparison.Ordinal)
            && Rect.Matches(other.Rect);
    }
}
