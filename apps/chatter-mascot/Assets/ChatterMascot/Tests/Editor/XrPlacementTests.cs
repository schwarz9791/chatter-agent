using ChatterMascot.Xr;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="XrPlacement.Solve"/> の不変条件を固定する。<b>具体値の突き合わせではない</b>——
    /// 求まった Origin を使って頭のローカル姿勢をワールドへ変換すると、
    /// 常に「距離とオフセットどおりにキャラの正面へ置いた頭の位置」に一致することを確かめる。
    /// </summary>
    [TestFixture]
    public sealed class XrPlacementTests
    {
        private const float Tolerance = 1e-4f;

        private static readonly float[] Yaws = { 0f, 90f, -135f, 179f };

        private static readonly Vector3[] HeadLocalPositions =
        {
            Vector3.zero,
            new Vector3(0.1f, 1.6f, -0.05f),
            new Vector3(-0.3f, 1.5f, 0.2f),
        };

        private static readonly float[] Azimuths = { 0f, 30f, -45f };

        [Test]
        public void SolvedOriginSatisfiesThePlacementInvariants()
        {
            var characterFeet = new Vector3(1f, 0f, 2f);
            const float distance = 0.6f;
            const float feetBelowEye = 0.4f;

            foreach (var yaw in Yaws)
            {
                foreach (var headLocal in HeadLocalPositions)
                {
                    foreach (var azimuth in Azimuths)
                    {
                        XrPlacement.Solve(headLocal, yaw, characterFeet, distance, azimuth, feetBelowEye,
                            out var originPosition, out var originYaw);

                        // ★ 検査は Origin を通した実際の頭の位置で行う。期待値を Solve と同じ式で
                        //   組み立てて比べると、式が壊れてもテストが一緒に壊れて落ちない
                        var head = originPosition + Quaternion.Euler(0f, originYaw, 0f) * headLocal;
                        var context = $"yaw={yaw} headLocal={headLocal} azimuth={azimuth}";

                        // 1. キャラは −Z を向いているので、頭はキャラの正面（−Z 側）に水平 distance
                        var horizontal = head - characterFeet;
                        horizontal.y = 0f;
                        Assert.That(horizontal.magnitude, Is.EqualTo(distance).Within(Tolerance), context);
                        Assert.That(Vector3.Distance(horizontal.normalized, Vector3.back), Is.LessThan(Tolerance), context);

                        // 2. 頭の高さ − 足元の高さ = feetBelowEye
                        Assert.That(head.y - characterFeet.y, Is.EqualTo(feetBelowEye).Within(Tolerance), context);

                        // 3. 頭の正面を azimuth だけ右へ回すとキャラの方向（+Z）を向く
                        var headWorldYaw = originYaw + yaw;
                        Assert.That(NormalizeDegrees(headWorldYaw + azimuth), Is.EqualTo(0f).Within(Tolerance), context);
                    }
                }
            }
        }

        private static float NormalizeDegrees(float degrees)
        {
            var wrapped = degrees % 360f;
            if (wrapped > 180f) wrapped -= 360f;
            if (wrapped < -180f) wrapped += 360f;
            return wrapped;
        }
    }
}
