using ChatterMascot.Xr;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class XrBoardRulesTests
    {
        private const float Tolerance = 1e-4f;
        private static readonly Vector3 Head = new Vector3(0f, 1.6f, 0f);
        private static readonly Vector2 Size = new Vector2(1.2f, 0.75f);

        // 板は +Z 方向 1.1m、視点へ正対（板の +Z は視点と反対側を向く）
        private static readonly Vector3 Center = new Vector3(0f, 1.6f, 1.1f);
        private static readonly Quaternion Facing = Quaternion.identity;

        private static Ray RayTo(Vector3 target) => new Ray(Head, (target - Head).normalized);

        // ── InitialPose ──────────────────────────────────

        [Test]
        public void InitialPoseIsInFrontAtEyeHeightAndDistance()
        {
            var viewer = new Vector3(1f, 1.6f, 2f);
            var (position, rotation) = XrBoardRules.InitialPose(viewer, Vector3.right);
            Assert.That(position.x, Is.EqualTo(1f + XrBoardRules.DefaultDistance).Within(Tolerance));
            Assert.That(position.y, Is.EqualTo(1.6f).Within(Tolerance));
            Assert.That(position.z, Is.EqualTo(2f).Within(Tolerance));
            Assert.That(Vector3.Dot(rotation * Vector3.forward, Vector3.right), Is.EqualTo(1f).Within(Tolerance));
            Assert.That((rotation * Vector3.up).y, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void InitialPoseStaysLevelWhenLookingDown()
        {
            var (position, _) = XrBoardRules.InitialPose(Head, new Vector3(0f, -1f, 1f));
            Assert.That(position.y, Is.EqualTo(Head.y).Within(Tolerance));
            Assert.That(Vector3.Distance(position, Head), Is.EqualTo(XrBoardRules.DefaultDistance).Within(Tolerance));
        }

        [Test]
        public void InitialPoseFallsBackWhenLookingStraightDown()
        {
            var (position, rotation) = XrBoardRules.InitialPose(Head, Vector3.down);
            Assert.That(position.z, Is.EqualTo(XrBoardRules.DefaultDistance).Within(Tolerance));
            Assert.That(Vector3.Dot(rotation * Vector3.forward, Vector3.forward), Is.EqualTo(1f).Within(Tolerance));
        }

        // ── Pick ─────────────────────────────────────────

        [Test]
        public void PickHitsTheBody()
        {
            var part = XrBoardRules.Pick(RayTo(Center), Center, Facing, Size, true, out var distance);
            Assert.That(part, Is.EqualTo(BoardPart.Body));
            Assert.That(distance, Is.EqualTo(1.1f).Within(Tolerance));
        }

        [Test]
        public void PickMissesOutsideTheBoard()
        {
            var part = XrBoardRules.Pick(RayTo(Center + new Vector3(2f, 0f, 0f)), Center, Facing, Size, true, out _);
            Assert.That(part, Is.EqualTo(BoardPart.None));
        }

        [Test]
        public void PickPrefersTheCornerOverTheBody()
        {
            var corner = Center + new Vector3(Size.x / 2f - 0.01f, Size.y / 2f - 0.01f, 0f);
            Assert.That(XrBoardRules.Pick(RayTo(corner), Center, Facing, Size, true, out _), Is.EqualTo(BoardPart.Corner));
        }

        [Test]
        public void PickTakesTheCornerJustOutsideTheRectangle()
        {
            var outside = Center + new Vector3(Size.x / 2f + 0.02f, Size.y / 2f + 0.02f, 0f);
            Assert.That(XrBoardRules.Pick(RayTo(outside), Center, Facing, Size, true, out _), Is.EqualTo(BoardPart.Corner));
        }

        [Test]
        public void PickIgnoresCornersWhenDisabled()
        {
            var inside = Center + new Vector3(Size.x / 2f - 0.01f, Size.y / 2f - 0.01f, 0f);
            var outside = Center + new Vector3(Size.x / 2f + 0.02f, Size.y / 2f + 0.02f, 0f);
            Assert.That(XrBoardRules.Pick(RayTo(inside), Center, Facing, Size, false, out _), Is.EqualTo(BoardPart.Body));
            Assert.That(XrBoardRules.Pick(RayTo(outside), Center, Facing, Size, false, out _), Is.EqualTo(BoardPart.None));
        }

        [Test]
        public void PickMissesWhenTheRayPointsAway()
        {
            var away = new Ray(Head, Vector3.back);
            Assert.That(XrBoardRules.Pick(away, Center, Facing, Size, true, out _), Is.EqualTo(BoardPart.None));
        }

        // ── BoardDrag ────────────────────────────────────

        private static BoardDrag Begin(Vector3 grab, Vector3 center)
        {
            Assert.That(BoardDrag.TryBegin(RayTo(grab), Head, center, out var drag), Is.True);
            return drag;
        }

        [Test]
        public void DragWithTheSameRayDoesNotMove()
        {
            var grab = Center + new Vector3(0.4f, 0.2f, 0f);
            var drag = Begin(grab, Center);
            Assert.That(drag.TryMove(RayTo(grab), Head, out var position, out _), Is.True);
            Assert.That(Vector3.Distance(position, Center), Is.LessThan(1e-3f));
        }

        [Test]
        public void DragKeepsTheDistance()
        {
            var drag = Begin(Center, Center);
            Assert.That(drag.TryMove(RayTo(Center + new Vector3(1f, 0.3f, 0f)), Head, out var position, out _), Is.True);
            Assert.That(Vector3.Distance(position, Head), Is.EqualTo(1.1f).Within(1e-3f));
        }

        [Test]
        public void DragFromTheEdgeDoesNotJumpToTheCentre()
        {
            var edge = Center + new Vector3(0.55f, -0.3f, 0f);
            var drag = Begin(edge, Center);
            drag.TryMove(RayTo(edge + new Vector3(0.1f, 0f, 0f)), Head, out var position, out _);
            // 掴んだ点が動いたぶんだけ中心が動く（中心を指した位置へは飛ばない）
            Assert.That(position.x, Is.GreaterThan(0f));
            Assert.That(Vector3.Distance(position, edge), Is.GreaterThan(0.3f));
        }

        [Test]
        public void DragClampsElevation()
        {
            var drag = Begin(Center, Center);
            Assert.That(drag.TryMove(RayTo(Head + new Vector3(0f, 2f, 1f)), Head, out var position, out _), Is.True);
            var elevation = Mathf.Asin((position.y - Head.y) / Vector3.Distance(position, Head)) * Mathf.Rad2Deg;
            Assert.That(elevation, Is.EqualTo(XrBoardRules.MaxElevationDegrees).Within(1e-2f));
        }

        [Test]
        public void DragHoldsWhenPointingNearlyStraightUpOrDown()
        {
            var drag = Begin(Center, Center);
            Assert.That(drag.TryMove(RayTo(Head + new Vector3(0f, 5f, 1f)), Head, out _, out _), Is.False);
            Assert.That(drag.TryMove(RayTo(Head + new Vector3(0f, -5f, 1f)), Head, out _, out _), Is.False);
        }

        [Test]
        public void DragStillMovesJustBelowThePointLimit()
        {
            var drag = Begin(Center, Center);
            Assert.That(drag.TryMove(RayTo(Head + new Vector3(0.2f, 3f, 1f)), Head, out var position, out _), Is.True);
            Assert.That(position.x, Is.GreaterThan(0f));
        }

        [Test]
        public void DragFacesTheHeadWithoutRoll()
        {
            var drag = Begin(Center, Center);
            drag.TryMove(RayTo(Head + new Vector3(1f, 0.5f, 1f)), Head, out var position, out var rotation);
            Assert.That(Vector3.Dot(rotation * Vector3.forward, (position - Head).normalized), Is.EqualTo(1f).Within(Tolerance));
            Assert.That(Vector3.Dot(rotation * Vector3.right, Vector3.up), Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void DragReturnsFalseWhenTheRayMissesTheSphere()
        {
            var drag = Begin(Center, Center);
            Assert.That(drag.TryMove(new Ray(Head + new Vector3(10f, 0f, 0f), Vector3.forward), Head, out _, out _), Is.False);
        }

        [Test]
        public void DragKeepsTheRadiusWhenTheHeadMoves()
        {
            var drag = Begin(Center, Center);
            var moved = Head + new Vector3(0.3f, 0.1f, -0.2f);
            var ray = new Ray(moved, (Center - moved).normalized);
            Assert.That(drag.TryMove(ray, moved, out var position, out _), Is.True);
            Assert.That(Vector3.Distance(position, moved), Is.EqualTo(1.1f).Within(1e-3f));
        }

        [Test]
        public void BeginFailsWhenTheBoardIsAtTheHead()
        {
            Assert.That(BoardDrag.TryBegin(new Ray(Head, Vector3.forward), Head, Head, out _), Is.False);
        }

        [Test]
        public void BeginFailsWhenGrabbingNearlyStraightUp()
        {
            Assert.That(BoardDrag.TryBegin(RayTo(Head + new Vector3(0f, 5f, 1f)), Head, Center, out _), Is.False);
        }

        // ── Size ─────────────────────────────────────────

        [Test]
        public void SizeOfALandscapeVideoIsTheLongSideWide()
        {
            var size = XrBoardRules.Size(1.2f, 9f / 16f);
            Assert.That(size.x, Is.EqualTo(1.2f).Within(Tolerance));
            Assert.That(size.y, Is.EqualTo(0.675f).Within(Tolerance));
        }

        [Test]
        public void SizeOfAPortraitVideoIsTheLongSideTall()
        {
            var size = XrBoardRules.Size(1.2f, 16f / 9f);
            Assert.That(size.x, Is.EqualTo(0.675f).Within(Tolerance));
            Assert.That(size.y, Is.EqualTo(1.2f).Within(Tolerance));
        }

        [Test]
        public void SizeOfASquareVideoIsSquare()
        {
            var size = XrBoardRules.Size(1.2f, 1f);
            Assert.That(size.x, Is.EqualTo(1.2f).Within(Tolerance));
            Assert.That(size.y, Is.EqualTo(1.2f).Within(Tolerance));
        }

        // ── ResizedLongSide ─────────────────────────────────

        [Test]
        public void ResizedLongSideIsUnchangedAtTheSamePoint()
        {
            var grab = Center + new Vector3(0.6f, 0.375f, 0f);
            Assert.That(XrBoardRules.ResizedLongSide(1.2f, Center, grab, grab), Is.EqualTo(1.2f).Within(Tolerance));
        }

        [Test]
        public void ResizedLongSideScalesWithTheDistanceFromTheCentre()
        {
            var grab = Center + new Vector3(0.6f, 0.375f, 0f);
            var doubled = Center + new Vector3(1.2f, 0.75f, 0f);
            Assert.That(XrBoardRules.ResizedLongSide(1.2f, Center, grab, doubled), Is.EqualTo(2.4f).Within(Tolerance));
        }

        [Test]
        public void ResizedLongSideIsClamped()
        {
            var grab = Center + new Vector3(0.6f, 0.375f, 0f);
            Assert.That(XrBoardRules.ResizedLongSide(1.2f, Center, grab, Center + (grab - Center) * 10f),
                Is.EqualTo(XrBoardRules.MaxLongSide));
            Assert.That(XrBoardRules.ResizedLongSide(1.2f, Center, grab, Center + (grab - Center) * 0.01f),
                Is.EqualTo(XrBoardRules.MinLongSide));
        }

        [Test]
        public void ResizedLongSideStopsAtTheMinimumOnTheOtherSide()
        {
            var grab = Center + new Vector3(0.6f, 0.375f, 0f);
            Assert.That(XrBoardRules.ResizedLongSide(1.2f, Center, grab, Center - (grab - Center)),
                Is.EqualTo(XrBoardRules.MinLongSide));
        }

        // ── DimmingReached ──────────────────────────────────────────

        private static readonly float[] FineLevels = { 0.0039f, 0.25f, 0.5f, 0.75f, 0.9961f };

        [Test]
        public void DimmingReachedWhenTheValueEqualsTheRequest()
        {
            Assert.That(XrBoardRules.DimmingReached(0.5f, 0.5f, FineLevels), Is.True);
        }

        [Test]
        public void DimmingReachedAtTheRoundedEnds()
        {
            Assert.That(XrBoardRules.DimmingReached(0.0039f, 0f, FineLevels), Is.True);
            Assert.That(XrBoardRules.DimmingReached(0.9961f, 1f, FineLevels), Is.True);
        }

        [Test]
        public void DimmingNotReachedForAStaleValue()
        {
            Assert.That(XrBoardRules.DimmingReached(0.5f, 0.75f, FineLevels), Is.False);
        }

        [Test]
        public void DimmingReachedComparesWithTheClosestOfCoarseLevels()
        {
            var levels = new[] { 0f, 0.4f, 1f };
            Assert.That(XrBoardRules.DimmingReached(0.4f, 0.5f, levels), Is.True);
            Assert.That(XrBoardRules.DimmingReached(1f, 0.5f, levels), Is.False);
        }

        [Test]
        public void DimmingReachedAcceptsEitherOfEquallyCloseLevels()
        {
            var levels = new[] { 0f, 0.5f, 1f };
            Assert.That(XrBoardRules.DimmingReached(0f, 0.25f, levels), Is.True);
            Assert.That(XrBoardRules.DimmingReached(0.5f, 0.25f, levels), Is.True);
            Assert.That(XrBoardRules.DimmingReached(1f, 0.25f, levels), Is.False);
        }

        [Test]
        public void DimmingReachedAcceptsTheLevelBelowTheRequest()
        {
            var levels = new[] { 0.0039f, 0.4f, 0.9961f };
            Assert.That(XrBoardRules.DimmingReached(0.0039f, 0.25f, levels), Is.True);
            Assert.That(XrBoardRules.DimmingReached(0.4f, 0.75f, levels), Is.True);
            Assert.That(XrBoardRules.DimmingReached(0.0039f, 0.75f, levels), Is.False);
        }

        [Test]
        public void DimmingNotReachedForTheLevelAboveThatIsNotTheClosest()
        {
            var levels = new[] { 0.0039f, 0.4f, 0.9961f };
            Assert.That(XrBoardRules.DimmingReached(0.9961f, 0.5f, levels), Is.False);
        }

        [Test]
        public void DimmingReachedWithoutLevelsComparesWithTheRequest()
        {
            var none = new float[0];
            Assert.That(XrBoardRules.DimmingReached(0.25f, 0.25f, none), Is.True);
            Assert.That(XrBoardRules.DimmingReached(0.5f, 0.25f, none), Is.False);
            Assert.That(XrBoardRules.DimmingReached(0.0039f, 0f, none), Is.True);
        }
    }
}
