using ChatterMascot.Window;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class PointRectTests
    {
        private static readonly PointRect Primary = new PointRect(0f, 0f, 3840f, 2130f);

        [Test]
        public void RejectsRectanglesThatAreNotRectangles()
        {
            Assert.That(new PointRect(0f, 0f, 300f, 480f).IsValid, Is.True);
            Assert.That(new PointRect(0f, 0f, 0f, 480f).IsValid, Is.False);
            Assert.That(new PointRect(0f, 0f, 300f, -1f).IsValid, Is.False);
            Assert.That(new PointRect(float.NaN, 0f, 300f, 480f).IsValid, Is.False);
            Assert.That(new PointRect(0f, float.PositiveInfinity, 300f, 480f).IsValid, Is.False);
        }

        [Test]
        public void IntersectReturnsTheOverlap()
        {
            var window = new PointRect(3700f, 2000f, 300f, 480f);
            var visible = window.Intersect(Primary);

            Assert.That(visible.X, Is.EqualTo(3700f));
            Assert.That(visible.Y, Is.EqualTo(2000f));
            Assert.That(visible.Width, Is.EqualTo(140f));
            Assert.That(visible.Height, Is.EqualTo(130f));
        }

        [Test]
        public void IntersectReturnsAnEmptyRectWhenThereIsNoOverlap()
        {
            var window = new PointRect(5000f, 5000f, 300f, 480f);
            Assert.That(window.Intersect(Primary).Area, Is.EqualTo(0f));
        }

        /// <summary>接しているだけ（幅 0）は「重なっていない」として扱う。</summary>
        [Test]
        public void TouchingEdgesDoNotCount()
        {
            var window = new PointRect(3840f, 0f, 300f, 480f);
            Assert.That(window.Intersect(Primary).Area, Is.EqualTo(0f));
        }

        [Test]
        public void ClampIntoMovesWithoutResizing()
        {
            var window = new PointRect(3700f, 2000f, 300f, 480f);
            var clamped = window.ClampInto(Primary);

            Assert.That(clamped.Width, Is.EqualTo(300f));
            Assert.That(clamped.Height, Is.EqualTo(480f));
            Assert.That(clamped.X, Is.EqualTo(3540f));
            Assert.That(clamped.Y, Is.EqualTo(1650f));
        }

        [Test]
        public void ClampIntoPullsBackFromTheNegativeSide()
        {
            var window = new PointRect(-500f, -500f, 300f, 480f);
            var clamped = window.ClampInto(Primary);

            Assert.That(clamped.X, Is.EqualTo(0f));
            Assert.That(clamped.Y, Is.EqualTo(0f));
        }

        /// <summary>
        /// ★ 入りきらない軸は最小コーナーへ。上へはみ出す（メニューバーに潜って掴めない）より
        /// 下へはみ出す（頭が見えている）方がまし。
        /// </summary>
        [Test]
        public void ClampIntoAnchorsToTheMinCornerWhenItDoesNotFit()
        {
            var tall = new PointRect(100f, 100f, 5000f, 5000f);
            var clamped = tall.ClampInto(Primary);

            Assert.That(clamped.X, Is.EqualTo(0f));
            Assert.That(clamped.Y, Is.EqualTo(0f));
            Assert.That(clamped.Width, Is.EqualTo(5000f), "大きさは変えない");
        }

        // ── 大きさの規則 ──────────────────────────────────────────────

        [Test]
        public void WithMinimumSizeKeepsThePosition()
        {
            var grown = new PointRect(100f, 100f, 10f, 10f).WithMinimumSize(120f, 160f);

            Assert.That(grown, Is.EqualTo(new PointRect(100f, 100f, 120f, 160f)));
        }

        [Test]
        public void WithMinimumSizeLeavesABigEnoughRectAlone()
        {
            var rect = new PointRect(100f, 100f, 300f, 480f);

            Assert.That(rect.WithMinimumSize(120f, 160f), Is.EqualTo(rect));
        }

        [Test]
        public void WithSizeFittingIntoShrinksWithoutMoving()
        {
            var shrunk = new PointRect(1041f, -1111f, 3000f, 2000f)
                .WithSizeFittingInto(new PointRect(1041f, -1111f, 1800f, 1072f));

            Assert.That(shrunk, Is.EqualTo(new PointRect(1041f, -1111f, 1800f, 1072f)));
        }

        /// <summary>
        /// ★ <b>大きさを持たない領域は無視する。</b> モニタの情報が取れないフレームで
        /// 0 に潰すと、掴めない窓ができる。
        /// </summary>
        [Test]
        public void WithSizeFittingIntoIgnoresADegenerateBounds()
        {
            var rect = new PointRect(100f, 100f, 300f, 480f);

            Assert.That(rect.WithSizeFittingInto(default), Is.EqualTo(rect));
        }

        [Test]
        public void AtBottomCenterOfSitsOnTheBottomEdge()
        {
            var window = new PointRect(9999f, 9999f, 300f, 480f);
            var placed = window.AtBottomCenterOf(Primary);

            Assert.That(placed.X, Is.EqualTo((3840f - 300f) / 2f));
            Assert.That(placed.Y, Is.EqualTo(0f), "作業領域の下端。メニューバーから最も遠い");
        }

        // ── アスペクトの移行（#88。既定が 5:8 → 1:1 に変わったときの保存済み矩形の直し方） ──

        [Test]
        public void WithAspectKeepingHeightKeepsTheHeight()
        {
            var rect = new PointRect(1000f, 500f, 300f, 480f);
            var migrated = rect.WithAspectKeepingHeight(540f, 540f);

            Assert.That(migrated.Height, Is.EqualTo(480f));
        }

        [Test]
        public void WithAspectKeepingHeightMatchesTheGivenAspect()
        {
            var rect = new PointRect(1000f, 500f, 300f, 480f);
            var migrated = rect.WithAspectKeepingHeight(540f, 540f);

            // 540:540 は 1:1 なので、新しい幅は高さ（480）そのままになる
            Assert.That(migrated.Width, Is.EqualTo(480f).Within(0.001f));
        }

        /// <summary>
        /// ★ <b>下端中央は動かさない。</b> <c>Y</c>（最小コーナー・bottom-up）は高さを
        ///   変えないここでは触らない。中心の X は、幅の差の半分だけ詰める側へ寄せて保つ。
        /// </summary>
        [Test]
        public void WithAspectKeepingHeightKeepsTheBottomCenter()
        {
            var rect = new PointRect(1000f, 500f, 300f, 480f);
            var migrated = rect.WithAspectKeepingHeight(540f, 540f);

            Assert.That(migrated.Y, Is.EqualTo(rect.Y), "下端は動かさない");
            Assert.That(migrated.X + migrated.Width * 0.5f,
                        Is.EqualTo(rect.X + rect.Width * 0.5f).Within(0.001f), "中心は動かさない");
        }

        /// <summary>★ アスペクトが定義できない入力では何もしない。</summary>
        [Test]
        public void WithAspectKeepingHeightIsANoOpForDegenerateAspect()
        {
            var rect = new PointRect(1000f, 500f, 300f, 480f);

            Assert.That(rect.WithAspectKeepingHeight(0f, 480f), Is.EqualTo(rect));
            Assert.That(rect.WithAspectKeepingHeight(300f, 0f), Is.EqualTo(rect));
            Assert.That(rect.WithAspectKeepingHeight(-1f, 480f), Is.EqualTo(rect));
        }

        /// <summary>★ ロケールに引きずられないこと（`0,5` になると指紋の比較が壊れる）。</summary>
        [Test]
        public void ToStringUsesInvariantFormatting()
        {
            Assert.That(new PointRect(1041f, -1111f, 1800f, 1072f).ToString(),
                        Is.EqualTo("1041,-1111 1800x1072"));
        }
    }
}
