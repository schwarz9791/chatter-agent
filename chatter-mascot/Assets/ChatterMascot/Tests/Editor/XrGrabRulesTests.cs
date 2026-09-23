using ChatterMascot.Xr;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="XrGrabRules.IsPinching"/> のヒステリシス、<see cref="XrGrabRules.HeldDistance"/> の
    /// 奥行き、<see cref="XrGrabRules.TryYawToFace"/> のヨー。
    /// </summary>
    [TestFixture]
    public sealed class XrGrabRulesTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void EntersPinchOnlyAtOrAboveTheEnterThreshold()
        {
            Assert.That(XrGrabRules.IsPinching(false, XrGrabRules.PinchEnterValue), Is.True);
            Assert.That(XrGrabRules.IsPinching(false, XrGrabRules.PinchEnterValue - 0.001f), Is.False);
        }

        [Test]
        public void ExitsPinchOnlyBelowTheExitThreshold()
        {
            Assert.That(XrGrabRules.IsPinching(true, XrGrabRules.PinchExitValue), Is.True);
            Assert.That(XrGrabRules.IsPinching(true, XrGrabRules.PinchExitValue - 0.001f), Is.False);
        }

        /// <summary>入りと抜けの間（ヒステリシス帯）では、直前の状態を保つ。</summary>
        [Test]
        public void HoldsThePreviousStateInsideTheHysteresisBand()
        {
            var midBand = (XrGrabRules.PinchEnterValue + XrGrabRules.PinchExitValue) / 2f;
            Assert.That(XrGrabRules.IsPinching(true, midBand), Is.True);
            Assert.That(XrGrabRules.IsPinching(false, midBand), Is.False);
        }

        private static readonly Vector3[] FromPoints =
        {
            Vector3.zero,
            new Vector3(0.3f, 1.5f, -0.2f),
            new Vector3(-1f, 0.9f, 2f),
        };

        [TestCase(0f, 1f)]    // +Z
        [TestCase(1f, 0f)]    // +X
        [TestCase(0f, -1f)]   // -Z
        [TestCase(-1f, 0f)]   // -X
        [TestCase(1f, 1f)]    // 斜め
        [TestCase(-1f, -1f)]  // 斜め
        [TestCase(-1f, 1f)]   // 斜め
        [TestCase(1f, -1f)]   // 斜め
        public void YawFacesTheHorizontalDirectionToTheTarget(float dx, float dz)
        {
            foreach (var from in FromPoints)
            {
                // 高さも変えて、水平方向だけが効くことを確かめる
                var to = from + new Vector3(dx, 2.5f, dz);
                var ok = XrGrabRules.TryYawToFace(from, to, out var yaw);
                Assert.That(ok, Is.True);

                // ★ 期待値を実装と同じ式で組まないこと（XrPlacementTests と同じ方針）。
                //   「ローカル −Z（Vector3.back）を yaw で回すと、水平方向が to を向く」という
                //   不変条件で検査する。
                var facing = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
                var expected = new Vector3(dx, 0f, dz).normalized;
                Assert.That(Vector3.Distance(facing, expected), Is.LessThan(Tolerance));
            }
        }

        [Test]
        public void YawIsUndefinedWhenTheTargetIsDirectlyAboveOrBelow()
        {
            var from = new Vector3(1f, 0f, 2f);

            Assert.That(XrGrabRules.TryYawToFace(from, from + Vector3.up * 3f, out _), Is.False);
            Assert.That(XrGrabRules.TryYawToFace(from, from + Vector3.down * 3f, out _), Is.False);
        }

        private static readonly Vector3 HandPosition = new Vector3(0f, 1.2f, 0f);

        /// <summary>手から前方 <paramref name="horizontal"/>、下へ <paramref name="drop"/> の点へ向かうレイ。</summary>
        private static Ray RayTo(float horizontal, float drop) =>
            new Ray(HandPosition, new Vector3(0f, -drop, horizontal));

        /// <summary>掴んだ瞬間のレイでは、掴んだ距離をそのまま返す（掴んだ途端に跳ばない）。</summary>
        [TestCase(1f, 0.8f)]   // 急な俯角（交点を追う側）
        [TestCase(1f, 0.25f)]  // 混ぜる帯の中
        [TestCase(1f, -0.3f)]  // 上向き
        public void HeldDistanceEqualsTheGrabDistanceAtTheMomentOfGrab(float horizontal, float drop)
        {
            var ray = RayTo(horizontal, drop);
            var grabDistance = new Vector2(horizontal, drop).magnitude;
            var grabHeight = HandPosition.y - drop;

            Assert.That(XrGrabRules.HeldDistance(ray, grabHeight, grabDistance),
                Is.EqualTo(grabDistance).Within(Tolerance));
        }

        /// <summary>十分に下を向いたレイでは、掴んだ高さの水平面との交点を追う —— 手を下げれば手前、上げれば奥。</summary>
        [Test]
        public void SteepRaysFollowTheGrabHeightPlane()
        {
            const float grabHeight = 0.4f;
            var grabDistance = new Vector2(1f, 0.8f).magnitude;

            foreach (var horizontal in new[] { 0.5f, 1f, 1.5f })
            {
                var ray = RayTo(horizontal, 0.8f);
                var point = ray.GetPoint(XrGrabRules.HeldDistance(ray, grabHeight, grabDistance));

                Assert.That(point.y, Is.EqualTo(grabHeight).Within(Tolerance));
                Assert.That(point.z, Is.EqualTo(horizontal).Within(Tolerance));
            }
        }

        [Test]
        public void ShallowOrUpwardRaysKeepTheGrabDistance()
        {
            const float grabDistance = 1.3f;

            foreach (var ray in new[] { RayTo(1f, 0.1f), RayTo(1f, 0f), RayTo(1f, -0.5f) })
            {
                Assert.That(XrGrabRules.HeldDistance(ray, 0.4f, grabDistance),
                    Is.EqualTo(grabDistance).Within(Tolerance));
            }
        }

        /// <summary>俯角を連続に変えたとき、距離が跳ばない（混ぜる帯の両端でつながる）。</summary>
        [Test]
        public void HeldDistanceIsContinuousAcrossTheBlendBand()
        {
            const float grabHeight = 0.4f;
            const float grabDistance = 1f;
            const float step = 0.05f;

            var previous = XrGrabRules.HeldDistance(RayTo(1f, 0f), grabHeight, grabDistance);
            for (var degrees = step; degrees <= 45f; degrees += step)
            {
                var drop = Mathf.Tan(degrees * Mathf.Deg2Rad);
                var current = XrGrabRules.HeldDistance(RayTo(1f, drop), grabHeight, grabDistance);
                Assert.That(Mathf.Abs(current - previous), Is.LessThan(0.02f), $"{degrees:F2}°");
                previous = current;
            }
        }

        [Test]
        public void HeldDistanceIsCappedForNearlyHorizontalRays()
        {
            // 混ぜる帯を抜けた浅い俯角で、掴んだ高さが手より十分に下なので、交点は上限より遠い
            var ray = RayTo(1f, 0.4f);
            var distance = XrGrabRules.HeldDistance(ray, HandPosition.y - 2f, 0.5f);

            Assert.That(distance, Is.EqualTo(XrGrabRules.MaxHeldDistance).Within(Tolerance));
        }

        /// <summary>手の高さを面の高さの前後でなめらかに動かしても、距離が跳ばない。</summary>
        [Test]
        public void HeldDistanceIsContinuousAcrossThePlaneHeight()
        {
            const float planeHeight = 0.4f;
            const float grabDistance = 1f;
            const float step = 0.001f;
            var direction = new Vector3(0f, -0.8f, 1f).normalized;

            var y = planeHeight - 0.2f;
            var previous = XrGrabRules.HeldDistance(new Ray(new Vector3(0f, y, 0f), direction), planeHeight, grabDistance);
            for (y += step; y <= planeHeight + 0.5f; y += step)
            {
                var current = XrGrabRules.HeldDistance(new Ray(new Vector3(0f, y, 0f), direction), planeHeight, grabDistance);
                Assert.That(Mathf.Abs(current - previous), Is.LessThan(0.02f), $"y={y:F3}");
                previous = current;
            }
        }

        /// <summary>手が面の高さぎりぎり上でも、掴んだ距離の近くを保つ（手元へ吸い込まれない）。</summary>
        [Test]
        public void HeldDistanceStaysNearGrabDistanceJustAbovePlaneHeight()
        {
            const float planeHeight = 0.4f;
            const float grabDistance = 1f;
            var direction = new Vector3(0f, -0.8f, 1f).normalized;

            Assert.That(
                XrGrabRules.HeldDistance(new Ray(new Vector3(0f, planeHeight, 0f), direction), planeHeight, grabDistance),
                Is.EqualTo(grabDistance).Within(Tolerance));

            var justAbove = XrGrabRules.HeldDistance(
                new Ray(new Vector3(0f, planeHeight + 0.001f, 0f), direction), planeHeight, grabDistance);
            Assert.That(justAbove, Is.GreaterThan(grabDistance * 0.9f));
        }
    }
}
