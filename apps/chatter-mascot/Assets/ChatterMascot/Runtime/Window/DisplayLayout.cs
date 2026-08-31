using System.Collections.Generic;
using System.Text;

namespace ChatterMascot.Window
{
    /// <summary>
    /// いま繋がっているディスプレイの構成。<b>データとして注入する</b>
    /// （<c>AssetEnv</c> と同じ形。<c>UniWindowController</c> を EditMode から呼べないため）。
    ///
    /// ★ <b>矩形は「作業領域（visible frame）」であってフルフレームではない。</b>
    ///   メニューバーや Dock の帯は含まれない（実測値は <c>docs/mascot.md</c>）。
    ///
    /// ★ <b>作業領域の和集合には隙間がある。</b> メニューバーや Dock の帯はどの矩形にも入らない
    ///   （実測でカーソルが <c>y=-23</c> という、どのモニタにも属さない位置に居た）。
    ///   <b>「どの矩形にも入らない＝画面外」と判定しないこと。</b>
    /// </summary>
    public sealed class DisplayLayout
    {
        /// <summary>
        /// ★ <b><c>[0]</c> がメインディスプレイ。</b> LibUniWinC は <c>NSScreen.screens</c> を
        ///   そのまま並べており、AppKit はその先頭を<b>メニューバーのある画面</b>と定めている。
        ///   実測でも <c>[0]</c> が原点 <c>(0,0)</c> の 4K だった。
        /// </summary>
        public const int PrimaryIndex = 0;

        public IReadOnlyList<PointRect> Monitors { get; }

        /// <summary>
        /// 構成が変わったかどうかだけを見るための指紋。
        ///
        /// ★ <b>比較にしか使わない。</b> 中身を解釈して復元先を決めようとしないこと ——
        ///   ディスプレイの並びは同じでも配置だけ変わることがあり、そのときは
        ///   <see cref="WindowPlacement"/> の可視判定に任せた方が素直。
        /// </summary>
        public string Signature { get; }

        /// <summary>モニタが1枚も取れないときは大きさ 0 を返す。呼び出し側で分岐すること。</summary>
        public PointRect Primary =>
            Monitors.Count > PrimaryIndex ? Monitors[PrimaryIndex] : default;

        public bool HasMonitors => Monitors.Count > 0;

        private DisplayLayout(IReadOnlyList<PointRect> monitors, string signature)
        {
            Monitors = monitors;
            Signature = signature;
        }

        public static DisplayLayout Of(IReadOnlyList<PointRect> monitors)
        {
            var list = monitors ?? new List<PointRect>();
            var sb = new StringBuilder(64);
            for (var i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(list[i].ToString());
            }
            return new DisplayLayout(list, sb.ToString());
        }
    }
}
