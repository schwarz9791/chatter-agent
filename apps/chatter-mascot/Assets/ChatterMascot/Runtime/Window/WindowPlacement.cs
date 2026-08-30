using System;

namespace ChatterMascot.Window
{
    /// <summary>なぜその位置になったか。ログと実機確認のために必ず出す。</summary>
    public enum PlacementReason
    {
        /// <summary>保存が無い / 壊れている。既定の場所へ。</summary>
        Defaulted,

        /// <summary>保存された位置をそのまま使った。</summary>
        Restored,

        /// <summary>ほとんどはみ出していたので、いちばん重なるモニタへ押し込んだ。</summary>
        Clamped,

        /// <summary>どのモニタとも重ならないので、メインへ置き直した。</summary>
        ReanchoredToPrimary,
    }

    public readonly struct Placement
    {
        public readonly PointRect Rect;
        public readonly PlacementReason Reason;

        /// <summary>基準にしたモニタ。決められなければ -1。</summary>
        public readonly int MonitorIndex;

        public Placement(PointRect rect, PlacementReason reason, int monitorIndex)
        {
            Rect = rect;
            Reason = reason;
            MonitorIndex = monitorIndex;
        }
    }

    /// <summary>
    /// 復元先を決めるときの制約。<b>すべてポイント。</b>
    /// </summary>
    public readonly struct PlacementLimits
    {
        public readonly float DefaultWidth;
        public readonly float DefaultHeight;
        public readonly float MinWidth;
        public readonly float MinHeight;

        /// <summary>
        /// 「掴める」と認める最小の可視矩形。
        ///
        /// ★ <b>面積比で判定しないこと。</b> 300x480 の縦長では「面積の 20% が見えている」を
        ///   満たしても、可視部が下端の帯だけということが起きる。**帯は掴めない。**
        /// </summary>
        public readonly float MinVisibleWidth;
        public readonly float MinVisibleHeight;

        /// <summary>
        /// ディスプレイ構成が変わっていたときに使う、厳しい方の閾値。
        ///
        /// ★ 構成が変わったなら、<b>薄く重なっているだけの位置は信用しない</b>。
        ///   前回と同じ構成なら、ユーザーが自分で端に寄せた結果かもしれないので尊重する。
        /// </summary>
        public readonly float StrictVisibleWidth;
        public readonly float StrictVisibleHeight;

        public PlacementLimits(
            float defaultWidth, float defaultHeight,
            float minWidth, float minHeight,
            float minVisibleWidth, float minVisibleHeight,
            float strictVisibleWidth, float strictVisibleHeight)
        {
            DefaultWidth = defaultWidth;
            DefaultHeight = defaultHeight;
            MinWidth = minWidth;
            MinHeight = minHeight;
            MinVisibleWidth = minVisibleWidth;
            MinVisibleHeight = minVisibleHeight;
            StrictVisibleWidth = strictVisibleWidth;
            StrictVisibleHeight = strictVisibleHeight;
        }
    }

    /// <summary>
    /// <b>保存された矩形 + いまのディスプレイ構成 → 復元先の矩形。</b> 純粋関数。
    ///
    /// ★ <b>これが要るのは、引き戻しを OS に任せられないから。</b> 実測では
    ///   <c>SetPosition</c> で画面外へ出しても macOS は引き戻さない
    ///   （<c>isFreePositioningEnabled</c> が false でも。→ <c>docs/mascot.md</c>）。
    ///   <b>つまり「画面外に置いたまま終了できる」</b>ので、次の起動で拾い直すのは
    ///   こちらの責任になる。これが無いと<b>窓が行方不明になる</b>。
    /// </summary>
    public static class WindowPlacement
    {
        public static Placement Resolve(WindowState saved, DisplayLayout now, PlacementLimits limits)
        {
            if (now == null || !now.HasMonitors)
            {
                // モニタが1枚も取れない。ここで既定へ寄せても置き場所が無いので、
                // 保存があればそのまま、無ければ原点に既定サイズで出す
                var fallback = saved.Rect.IsValid
                    ? saved.Rect
                    : new PointRect(0f, 0f, limits.DefaultWidth, limits.DefaultHeight);
                return new Placement(fallback, PlacementReason.Defaulted, -1);
            }

            var primary = now.Primary;

            if (!saved.Rect.IsValid)
            {
                var size = ClampSize(limits.DefaultWidth, limits.DefaultHeight, primary, limits);
                return new Placement(size.AtBottomCenterOf(primary), PlacementReason.Defaulted, DisplayLayout.PrimaryIndex);
            }

            // ★ 大きさを先に決める。位置の判定は「その大きさの窓が見えるか」なので、
            //   クランプ後の大きさでやらないと閾値がずれる
            var wanted = ClampSize(saved.Rect.Width, saved.Rect.Height, primary, limits)
                .WithPosition(saved.Rect.X, saved.Rect.Y);

            var best = -1;
            var bestArea = 0f;
            var bestVisible = default(PointRect);
            for (var i = 0; i < now.Monitors.Count; i++)
            {
                var visible = wanted.Intersect(now.Monitors[i]);
                if (visible.Area <= bestArea) continue;
                bestArea = visible.Area;
                best = i;
                bestVisible = visible;
            }

            if (best < 0)
            {
                var size = wanted.AtBottomCenterOf(primary);
                return new Placement(size, PlacementReason.ReanchoredToPrimary, DisplayLayout.PrimaryIndex);
            }

            var sameLayout = string.Equals(saved.DisplaySignature, now.Signature, StringComparison.Ordinal);
            var needWidth = sameLayout ? limits.MinVisibleWidth : limits.StrictVisibleWidth;
            var needHeight = sameLayout ? limits.MinVisibleHeight : limits.StrictVisibleHeight;

            if (bestVisible.Width >= needWidth && bestVisible.Height >= needHeight)
            {
                return new Placement(wanted, PlacementReason.Restored, best);
            }

            return new Placement(wanted.ClampInto(now.Monitors[best]), PlacementReason.Clamped, best);
        }

        /// <summary>
        /// 大きさを下限と<b>モニタの作業領域</b>で挟む。
        ///
        /// ★ 上限をモニタにするのは、<b>作業領域より大きい窓は必ずどこかがはみ出す</b>から。
        ///   はみ出したまま <see cref="PointRect.ClampInto"/> に渡すと最小コーナーに張り付く。
        /// </summary>
        private static PointRect ClampSize(float width, float height, PointRect monitor, PlacementLimits limits)
        {
            var w = Math.Max(limits.MinWidth, width);
            var h = Math.Max(limits.MinHeight, height);
            if (monitor.Width > 0f) w = Math.Min(w, monitor.Width);
            if (monitor.Height > 0f) h = Math.Min(h, monitor.Height);
            return new PointRect(0f, 0f, w, h);
        }
    }
}
