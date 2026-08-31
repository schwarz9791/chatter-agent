using System;

namespace ChatterMascot.Window
{
    /// <summary>なぜその位置になったか。ログと実機確認のために必ず出す。</summary>
    public enum PlacementReason
    {
        /// <summary>保存が無い / 壊れている。既定の大きさで既定の場所へ。</summary>
        Defaulted,

        /// <summary>
        /// 保存された位置をそのまま使った。
        ///
        /// ★ <b>モニタの情報が1枚も取れなかったときもこれ。</b> 事実として
        ///   「保存された位置をそのまま使った」であり、区別は
        ///   <see cref="Placement.MonitorIndex"/> が担う。
        /// </summary>
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

        /// <summary>
        /// 基準にしたモニタ。決められなければ <see cref="WindowPlacement.NoMonitor"/>。
        /// </summary>
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
        /// ★ <b>面積比で判定しないこと。</b> 縦長の窓では、面積の条件を満たしていても
        ///   <b>可視部が端の細い帯だけ</b>ということが起きる。<b>帯は掴めない。</b>
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
        /// <summary>
        /// 基準にしたモニタが決まらなかったことを表す。
        ///
        /// ★ <b>「モニタの情報が1枚も取れなかった」ことは、理由ではなくこれで読み取る。</b>
        ///   <see cref="PlacementReason"/> に値を足しても、ここで既に分かることしか増えない。
        /// </summary>
        public const int NoMonitor = -1;

        public static Placement Resolve(WindowState saved, DisplayLayout now, PlacementLimits limits)
        {
            var hasMonitors = now != null && now.HasMonitors;

            if (!saved.Rect.IsValid)
            {
                var fresh = new PointRect(0f, 0f, limits.DefaultWidth, limits.DefaultHeight);
                if (!hasMonitors) return new Placement(fresh, PlacementReason.Defaulted, NoMonitor);

                var target = now.Primary;
                return new Placement(
                    fresh.WithSizeFittingInto(target).AtBottomCenterOf(target),
                    PlacementReason.Defaulted, DisplayLayout.PrimaryIndex);
            }

            var wanted = saved.Rect.WithMinimumSize(limits.MinWidth, limits.MinHeight);

            if (!hasMonitors)
            {
                // ★ **位置は動かさない。** 置き場所が分からないのに動かすと、
                //   「モニタの情報が一瞬取れなかっただけ」で窓が飛ぶ。
                //   ★ 大きさの下限だけは効かせる —— 保存ファイルは人が編集しうるので、
                //   潰れた矩形をそのまま適用すると掴めない窓ができる。
                //   モニタが取れなかったことは <see cref="Placement.MonitorIndex"/> で読み取れる
                return new Placement(wanted, PlacementReason.Restored, NoMonitor);
            }

            // ★ **候補ごとに「そのモニタに収まる大きさ」で評価する。**
            //   「主モニタで削ってから選ぶ」と、選んだ先がもっと狭いときに削り足りず、
            //   そのまま復元しても押し込んでも**そのディスプレイからはみ出す**。
            //   ★ 「縮めてから可視をもう一度測る」を別の手順にしないこと ——
            //   別手順にすると測り直しを忘れる余地が残る。ここで一緒に決めれば構造的に消える。
            var best = NoMonitor;
            var bestArea = 0f;
            var bestRect = default(PointRect);
            var bestVisible = default(PointRect);
            for (var i = 0; i < now.Monitors.Count; i++)
            {
                var monitor = now.Monitors[i];
                var candidate = wanted.WithSizeFittingInto(monitor);
                var visible = candidate.Intersect(monitor);
                if (visible.Area <= bestArea) continue;

                bestArea = visible.Area;
                best = i;
                bestRect = candidate;
                bestVisible = visible;
            }

            if (best == NoMonitor)
            {
                var target = now.Primary;
                return new Placement(
                    wanted.WithSizeFittingInto(target).AtBottomCenterOf(target),
                    PlacementReason.ReanchoredToPrimary, DisplayLayout.PrimaryIndex);
            }

            // ★ 構成が変わっていたら厳しい方の閾値を使う。前と同じ構成なら、
            //   端に寄っているのはユーザーがそう置いた結果かもしれないので尊重する
            var sameLayout = string.Equals(saved.DisplaySignature, now.Signature, StringComparison.Ordinal);
            var needWidth = sameLayout ? limits.MinVisibleWidth : limits.StrictVisibleWidth;
            var needHeight = sameLayout ? limits.MinVisibleHeight : limits.StrictVisibleHeight;

            if (bestVisible.Width >= needWidth && bestVisible.Height >= needHeight)
            {
                return new Placement(bestRect, PlacementReason.Restored, best);
            }

            return new Placement(bestRect.ClampInto(now.Monitors[best]), PlacementReason.Clamped, best);
        }
    }
}
