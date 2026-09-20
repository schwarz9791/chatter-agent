using ChatterMascot.Xr;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="XrGrabRules.IsPinching"/> のヒステリシスと <see cref="XrGrabRules.TryYawToFace"/> のヨー。
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
    }
}
