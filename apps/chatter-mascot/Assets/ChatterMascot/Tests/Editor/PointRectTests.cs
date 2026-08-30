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

        [Test]
        public void AtBottomCenterOfSitsOnTheBottomEdge()
        {
            var window = new PointRect(9999f, 9999f, 300f, 480f);
            var placed = window.AtBottomCenterOf(Primary);

            Assert.That(placed.X, Is.EqualTo((3840f - 300f) / 2f));
            Assert.That(placed.Y, Is.EqualTo(0f), "作業領域の下端。メニューバーから最も遠い");
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
