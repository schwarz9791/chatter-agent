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
    }
}
