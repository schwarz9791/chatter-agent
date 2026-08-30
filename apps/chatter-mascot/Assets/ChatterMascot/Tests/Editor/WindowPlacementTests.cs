using System.Collections.Generic;
using ChatterMascot.Window;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 実測に基づく構成で固定する（2026-08-30 / 外部 4K + 内蔵 Retina）:
    /// <c>[0]=(0,0 3840x2130)</c> ★ 2160 ではなく<b>作業領域</b> / <c>[1]=(1041,-1111 1800x1072)</c>。
    /// </summary>
    [TestFixture]
    public sealed class WindowPlacementTests
    {
        private static readonly PointRect Primary = new PointRect(0f, 0f, 3840f, 2130f);
        private static readonly PointRect Secondary = new PointRect(1041f, -1111f, 1800f, 1072f);

        private static readonly PlacementLimits Limits = new PlacementLimits(
            defaultWidth: 300f, defaultHeight: 480f,
            minWidth: 120f, minHeight: 120f,
            minVisibleWidth: 96f, minVisibleHeight: 96f,
            strictVisibleWidth: 160f, strictVisibleHeight: 160f);

        private static DisplayLayout Layout(params PointRect[] monitors) =>
            DisplayLayout.Of(new List<PointRect>(monitors));

        private static readonly DisplayLayout TwoDisplays = Layout(Primary, Secondary);

        private static WindowState Saved(PointRect rect, string signature = null) =>
            new WindowState(rect, signature ?? TwoDisplays.Signature);

        // ── 保存が無い / 壊れている ────────────────────────────────────

        [Test]
        public void UsesTheDefaultWhenNothingIsSaved()
        {
            var placement = WindowPlacement.Resolve(WindowState.None, TwoDisplays, Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.Defaulted));
            Assert.That(placement.Rect.Width, Is.EqualTo(300f));
            Assert.That(placement.Rect.Height, Is.EqualTo(480f));
            Assert.That(placement.Rect.Y, Is.EqualTo(0f), "主モニタの作業領域の下端");
            Assert.That(placement.Rect.X, Is.EqualTo((3840f - 300f) / 2f));
        }

        [Test]
        public void UsesTheDefaultWhenTheSavedRectIsGarbage()
        {
            var placement = WindowPlacement.Resolve(
                Saved(new PointRect(float.NaN, 0f, 300f, 480f)), TwoDisplays, Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.Defaulted));
        }

        // ── そのまま復元 ──────────────────────────────────────────────

        [Test]
        public void RestoresAWindowThatIsFullyOnScreen()
        {
            var rect = new PointRect(1770f, 1598f, 300f, 480f);
            var placement = WindowPlacement.Resolve(Saved(rect), TwoDisplays, Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.Restored));
            Assert.That(placement.Rect, Is.EqualTo(rect));
            Assert.That(placement.MonitorIndex, Is.EqualTo(0));
        }

        [Test]
        public void RestoresAWindowOnTheSecondDisplay()
        {
            var rect = new PointRect(1200f, -900f, 300f, 480f);
            var placement = WindowPlacement.Resolve(Saved(rect), TwoDisplays, Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.Restored));
            Assert.That(placement.MonitorIndex, Is.EqualTo(1));
        }

        /// <summary>
        /// 端に寄せてあるだけなら尊重する。ユーザーが自分で置いた可能性がある。
        /// </summary>
        [Test]
        public void RestoresAWindowThatHangsOffTheEdgeButIsStillGrabbable()
        {
            // 右へ 140pt 残す / 上へ 130pt 残す → どちらも 96pt を超える
            var rect = new PointRect(3700f, 2000f, 300f, 480f);
            var placement = WindowPlacement.Resolve(Saved(rect), TwoDisplays, Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.Restored));
        }

        // ── 押し戻し ──────────────────────────────────────────────────

        /// <summary>
        /// ★ <b>面積比で判定していたら通ってしまうケース。</b> 300x480 の窓のうち
        /// 300x40 = 12,000pt²（面積の 8%）が見えているが、<b>見えているのは下端の帯だけ</b>で掴めない。
        /// 高さの閾値（96pt）で落ちる。
        /// </summary>
        [Test]
        public void ClampsWhenOnlyAThinBandIsVisible()
        {
            var rect = new PointRect(1000f, 2090f, 300f, 480f);
            var placement = WindowPlacement.Resolve(Saved(rect), TwoDisplays, Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.Clamped));
            Assert.That(placement.Rect.Width, Is.EqualTo(300f), "大きさは変えない");
            Assert.That(placement.Rect.MaxY, Is.EqualTo(2130f), "作業領域の上端に収まる");
            Assert.That(placement.Rect.X, Is.EqualTo(1000f), "収まっている軸は動かさない");
        }

        [Test]
        public void ClampsIntoTheMonitorItOverlapsMost()
        {
            // 第2ディスプレイの左下へ、ほとんどはみ出した位置。
            // 可視は 159x91 —— 高さが 96pt に届かないので押し戻す
            var rect = new PointRect(900f, -1500f, 300f, 480f);
            var placement = WindowPlacement.Resolve(Saved(rect), TwoDisplays, Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.Clamped));
            Assert.That(placement.MonitorIndex, Is.EqualTo(1), "主モニタとは1ptも重なっていない");
            Assert.That(placement.Rect.X, Is.EqualTo(1041f));
            Assert.That(placement.Rect.Y, Is.EqualTo(-1111f));
        }

        // ── どのモニタとも重ならない ────────────────────────────────────

        /// <summary>
        /// ★ <b>これが「窓が行方不明になる」を防ぐ唯一の経路。</b> 実測では
        /// <c>SetPosition</c> で画面外へ出しても macOS は引き戻さないので、
        /// 画面外に置いたまま終了できてしまう。
        /// </summary>
        [Test]
        public void ReanchorsToPrimaryWhenNothingOverlaps()
        {
            var rect = new PointRect(9000f, 9000f, 300f, 480f);
            var placement = WindowPlacement.Resolve(Saved(rect), TwoDisplays, Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.ReanchoredToPrimary));
            Assert.That(placement.MonitorIndex, Is.EqualTo(0));
            Assert.That(placement.Rect.Y, Is.EqualTo(0f));
        }

        /// <summary>ディスプレイを1枚外したケース。第2ディスプレイの位置は拾えない。</summary>
        [Test]
        public void ReanchorsWhenTheDisplayItWasOnIsGone()
        {
            var rect = new PointRect(1200f, -900f, 300f, 480f);
            var placement = WindowPlacement.Resolve(Saved(rect), Layout(Primary), Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.ReanchoredToPrimary));
        }

        // ── 構成が変わったときは厳しくする ──────────────────────────────

        [Test]
        public void UsesAStricterThresholdWhenTheDisplayLayoutChanged()
        {
            // 可視は 140x130。同じ構成なら Restored（96 を超える）だが、
            // 構成が変わっていれば 160 に届かないので押し戻す
            var rect = new PointRect(3700f, 2000f, 300f, 480f);

            Assert.That(WindowPlacement.Resolve(Saved(rect), TwoDisplays, Limits).Reason,
                        Is.EqualTo(PlacementReason.Restored));
            Assert.That(WindowPlacement.Resolve(Saved(rect, "違う構成"), TwoDisplays, Limits).Reason,
                        Is.EqualTo(PlacementReason.Clamped));
        }

        // ── 大きさ ────────────────────────────────────────────────────

        [Test]
        public void ClampsTheSizeToTheMinimum()
        {
            var placement = WindowPlacement.Resolve(
                Saved(new PointRect(100f, 100f, 10f, 10f)), TwoDisplays, Limits);

            Assert.That(placement.Rect.Width, Is.EqualTo(120f));
            Assert.That(placement.Rect.Height, Is.EqualTo(120f));
        }

        [Test]
        public void ClampsTheSizeToTheWorkArea()
        {
            var placement = WindowPlacement.Resolve(
                Saved(new PointRect(0f, 0f, 99999f, 99999f)), TwoDisplays, Limits);

            Assert.That(placement.Rect.Width, Is.EqualTo(3840f));
            Assert.That(placement.Rect.Height, Is.EqualTo(2130f));
        }

        // ── モニタが取れない ──────────────────────────────────────────

        /// <summary>
        /// ★ <b>ここで既定へ寄せないこと。</b> 置き場所が分からないのに動かすと、
        /// 「モニタ情報が一瞬取れなかっただけ」で窓が飛ぶ。
        /// </summary>
        [Test]
        public void KeepsTheSavedRectWhenNoMonitorIsReported()
        {
            var rect = new PointRect(1770f, 1598f, 300f, 480f);
            var placement = WindowPlacement.Resolve(Saved(rect), Layout(), Limits);

            Assert.That(placement.Reason, Is.EqualTo(PlacementReason.Defaulted));
            Assert.That(placement.Rect, Is.EqualTo(rect));
            Assert.That(placement.MonitorIndex, Is.EqualTo(-1));
        }
    }
}
