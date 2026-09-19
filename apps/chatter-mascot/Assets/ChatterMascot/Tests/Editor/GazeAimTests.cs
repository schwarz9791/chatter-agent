using ChatterMascot.Protocol;
using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 視線の3状態（カーソル追従 / 自律的な漂い / <c>Prompt</c>）。
    ///
    /// ★ <c>Prompt</c> はカーソルの有無に関わらず常に正面（<c>Vector3.zero</c> / 頭 0）を見る
    ///   ——「身を乗り出して待つ」前傾は <c>VrmPoseAccent</c> の役目で、ここでは扱わない。
    /// </summary>
    [TestFixture]
    public sealed class GazeAimTests
    {
        private static GazeSample Sample(double now, GazeParams p, SpeechKind kind, Vector2? cursor)
        {
            return GazeAim.Evaluate(now, in p, kind, cursor);
        }

        /// <summary><c>Prompt</c> はカーソルが無くても、あっても常に原点・頭 0。</summary>
        [Test]
        public void PromptAlwaysLooksAtTheOriginRegardlessOfCursor()
        {
            var p = GazeParams.Default;

            var withoutCursor = Sample(3.0, p, SpeechKind.Prompt, null);
            Assert.That(withoutCursor.TargetLocalPosition, Is.EqualTo(Vector3.zero));
            Assert.That(withoutCursor.HeadPitchDegrees, Is.EqualTo(0f));
            Assert.That(withoutCursor.HeadYawDegrees, Is.EqualTo(0f));

            var withCursor = Sample(3.0, p, SpeechKind.Prompt, new Vector2(0.9f, -0.8f));
            Assert.That(withCursor.TargetLocalPosition, Is.EqualTo(Vector3.zero));
            Assert.That(withCursor.HeadPitchDegrees, Is.EqualTo(0f));
            Assert.That(withCursor.HeadYawDegrees, Is.EqualTo(0f));
        }

        /// <summary>カーソルが無ければ、振幅内で自律的に漂う。頭は動かさない。</summary>
        [Test]
        public void WandersWithinAmplitudeWhenThereIsNoCursor()
        {
            var p = GazeParams.Default;

            for (var t = 0.0; t < 60.0; t += 0.41)
            {
                var s = Sample(t, p, SpeechKind.Assistant, null);
                Assert.That(Mathf.Abs(s.TargetLocalPosition.x), Is.LessThanOrEqualTo(p.WanderMetersX + 1e-4f));
                Assert.That(Mathf.Abs(s.TargetLocalPosition.y), Is.LessThanOrEqualTo(p.WanderMetersY + 1e-4f));
                Assert.That(s.TargetLocalPosition.z, Is.EqualTo(0f));
                Assert.That(s.HeadPitchDegrees, Is.EqualTo(0f));
                Assert.That(s.HeadYawDegrees, Is.EqualTo(0f));
            }
        }

        /// <summary>カーソルありは、目・頭ともカーソルと同じ符号へ向く。</summary>
        [Test]
        public void FollowsTheCursorDirection()
        {
            var p = GazeParams.Default;
            var cursor = new Vector2(0.5f, -0.3f);

            var s = Sample(1.0, p, SpeechKind.Assistant, cursor);

            Assert.That(s.TargetLocalPosition.x, Is.GreaterThan(0f));
            Assert.That(s.TargetLocalPosition.y, Is.LessThan(0f));
            Assert.That(s.TargetLocalPosition.z, Is.EqualTo(0f));
            Assert.That(s.HeadYawDegrees, Is.GreaterThan(0f));
            Assert.That(s.HeadPitchDegrees, Is.LessThan(0f));
        }

        /// <summary>目標位置は <c>EyeReachMeters</c> を超えない（大きい感度・極端なカーソルでも）。</summary>
        [Test]
        public void ClampsTheEyeTargetToTheReach()
        {
            var p = new GazeParams(
                wanderSecondsX: 5.3f, wanderSecondsY: 8.7f,
                wanderMetersX: 0.25f, wanderMetersY: 0.15f,
                eyeSensitivity: 1f, headSensitivity: 0.1f,
                headPitchRangeDegrees: 25f, headYawRangeDegrees: 35f,
                eyeReachMeters: 0.6f, followSeconds: 0.15f);

            var s = Sample(0.0, p, SpeechKind.Assistant, new Vector2(1f, 1f));

            Assert.That(s.TargetLocalPosition.magnitude, Is.LessThanOrEqualTo(p.EyeReachMeters + 1e-4f));
            // 方向は保たれる（clamp で符号が反転しないこと）
            Assert.That(s.TargetLocalPosition.x, Is.GreaterThan(0f));
            Assert.That(s.TargetLocalPosition.y, Is.GreaterThan(0f));
        }

        /// <summary>
        /// 頭の角度は ±<c>HeadPitchRangeDegrees</c> / ±<c>HeadYawRangeDegrees</c> を超えない。
        ///
        /// ★ ここが渡している <c>headSensitivity: 10f</c> は<b>出荷値の100倍</b>で、
        ///   出荷値（<c>GazeParams.Default</c>）では clamp に到達しない。出荷値の挙動は
        ///   <see cref="UsesTheShippingRangePerUnitOfCursor"/> が固定する。
        /// </summary>
        [Test]
        public void ClampsHeadAnglesToTheirRange()
        {
            var p = new GazeParams(
                wanderSecondsX: 5.3f, wanderSecondsY: 8.7f,
                wanderMetersX: 0.25f, wanderMetersY: 0.15f,
                eyeSensitivity: 0.4f, headSensitivity: 10f,
                headPitchRangeDegrees: 25f, headYawRangeDegrees: 35f,
                eyeReachMeters: 0.6f, followSeconds: 0.15f);

            var s = Sample(0.0, p, SpeechKind.Assistant, new Vector2(1f, 1f));

            Assert.That(s.HeadPitchDegrees, Is.EqualTo(p.HeadPitchRangeDegrees));
            Assert.That(s.HeadYawDegrees, Is.EqualTo(p.HeadYawRangeDegrees));
        }

        /// <summary>
        /// ★ 出荷値（<c>GazeParams.Default</c>）での頭の可動域を固定する。
        ///   <see cref="ClampsHeadAnglesToTheirRange"/> は <c>headSensitivity</c> に出荷値の100倍を
        ///   渡していて、<b>出荷値の挙動を1本も固定していなかった</b>（PR #69 のレビューで判明）。
        ///   正規化カーソル 1.0（<c>CursorGazeSource</c> の固定 800pt 正規化で中心から 400pt）あたり、
        ///   yaw は 0.1 × 35 = 3.5°、pitch は 0.1 × 25 = 2.5°。
        /// </summary>
        [Test]
        public void UsesTheShippingRangePerUnitOfCursor()
        {
            var p = GazeParams.Default;
            var s = Sample(0.0, p, SpeechKind.Assistant, new Vector2(1f, 1f));

            Assert.That(s.HeadYawDegrees, Is.EqualTo(p.HeadSensitivity * p.HeadYawRangeDegrees).Within(1e-4f));
            Assert.That(s.HeadPitchDegrees, Is.EqualTo(p.HeadSensitivity * p.HeadPitchRangeDegrees).Within(1e-4f));
        }

        /// <summary>正面（<c>facing == toViewer</c>）では左右の基準は 0。</summary>
        [Test]
        public void NeutralYawIsZeroWhenAlreadyFacingTheViewer()
        {
            Assert.That(GazeAim.NeutralYawDegrees(Vector3.forward, Vector3.forward, 1f, 90f),
                Is.EqualTo(0f).Within(1e-4f));
        }

        /// <summary>
        /// 横へ回り込むと、返ったヨーぶん <paramref name="facing"/> を回すと
        /// <paramref name="toViewer"/> に近づく（角度が縮む）。
        ///
        /// ★ 期待値を <c>NeutralYawDegrees</c> と同じ式で組まないこと（<c>XrPlacementTests</c> と
        ///   同じ方針）。「回した後の残差角が、回す前より小さい」という不変条件で検査する。
        /// </summary>
        [TestCase(1f, 0f, 1f)]    // 右へ回り込む
        [TestCase(-1f, 0f, 1f)]   // 左へ回り込む
        [TestCase(1f, 0f, -1f)]   // 右後方へ回り込む
        public void NeutralYawTurnsTheHeadTowardTheViewer(float vx, float vy, float vz)
        {
            var facing = Vector3.forward;
            var toViewer = new Vector3(vx, vy, vz);

            var yaw = GazeAim.NeutralYawDegrees(facing, toViewer, 1f, 90f);
            Assert.That(yaw, Is.Not.EqualTo(0f));

            var beforeAngle = Vector3.Angle(facing, toViewer);
            var afterAngle = Vector3.Angle(Quaternion.AngleAxis(yaw, Vector3.up) * facing, toViewer);
            Assert.That(afterAngle, Is.LessThan(beforeAngle));
        }

        /// <summary><c>fraction</c> は倍率として素直に効く。</summary>
        [Test]
        public void NeutralYawScalesWithFraction()
        {
            var facing = Vector3.forward;
            var toViewer = new Vector3(1f, 0f, 1f);

            var half = GazeAim.NeutralYawDegrees(facing, toViewer, 0.5f, 90f);
            var full = GazeAim.NeutralYawDegrees(facing, toViewer, 1f, 90f);

            Assert.That(half, Is.EqualTo(full / 2f).Within(1e-3f));
        }

        /// <summary>±<c>maxDegrees</c> を超えない。</summary>
        [Test]
        public void NeutralYawClampsToItsRange()
        {
            var yaw = GazeAim.NeutralYawDegrees(Vector3.forward, Vector3.back, 1f, 30f);
            Assert.That(Mathf.Abs(yaw), Is.EqualTo(30f).Within(1e-4f));
        }

        /// <summary><c>facing</c> / <c>toViewer</c> の水平成分がほぼ 0 なら 0（atan2 の破綻を避ける）。</summary>
        [Test]
        public void NeutralYawIsZeroWhenEitherVectorHasNoHorizontalComponent()
        {
            Assert.That(GazeAim.NeutralYawDegrees(Vector3.up, new Vector3(1f, 0f, 1f), 1f, 90f), Is.EqualTo(0f));
            Assert.That(GazeAim.NeutralYawDegrees(Vector3.forward, Vector3.down, 1f, 90f), Is.EqualTo(0f));
        }

        /// <summary>微小な dt では漂いが不連続に跳ばない。</summary>
        [Test]
        public void IsContinuousOverASmallTimeStep()
        {
            var p = GazeParams.Default;
            const double dt = 1e-6;

            foreach (var t in new[] { 0.0, 4.4, 20.0 })
            {
                var a = Sample(t, p, SpeechKind.Assistant, null);
                var b = Sample(t + dt, p, SpeechKind.Assistant, null);

                Assert.That(Mathf.Abs(b.TargetLocalPosition.x - a.TargetLocalPosition.x), Is.LessThan(1e-4f));
                Assert.That(Mathf.Abs(b.TargetLocalPosition.y - a.TargetLocalPosition.y), Is.LessThan(1e-4f));
            }
        }

        /// <summary>感度・振幅・可動域をすべて 0 にすれば、カーソルの有無に関わらず恒等。</summary>
        [Test]
        public void AllZeroParamsYieldIdentity()
        {
            var zero = new GazeParams(
                wanderSecondsX: 5.3f, wanderSecondsY: 8.7f,
                wanderMetersX: 0f, wanderMetersY: 0f,
                eyeSensitivity: 0f, headSensitivity: 0f,
                headPitchRangeDegrees: 0f, headYawRangeDegrees: 0f,
                eyeReachMeters: 0f, followSeconds: 0.15f);

            var withoutCursor = Sample(12.3, zero, SpeechKind.Assistant, null);
            Assert.That(withoutCursor.TargetLocalPosition, Is.EqualTo(Vector3.zero));
            Assert.That(withoutCursor.HeadPitchDegrees, Is.EqualTo(0f));
            Assert.That(withoutCursor.HeadYawDegrees, Is.EqualTo(0f));

            var withCursor = Sample(12.3, zero, SpeechKind.Assistant, new Vector2(1f, -1f));
            Assert.That(withCursor.TargetLocalPosition, Is.EqualTo(Vector3.zero));
            Assert.That(withCursor.HeadPitchDegrees, Is.EqualTo(0f));
            Assert.That(withCursor.HeadYawDegrees, Is.EqualTo(0f));
        }

        /// <summary>
        /// <c>Smooth</c> はフレームレート非依存。同じ <c>deltaTime</c> の合計を
        /// 2分割で2回適用しても、1回で適用した結果とほぼ一致する。
        /// </summary>
        [Test]
        public void SmoothIsFrameRateIndependent()
        {
            const float current = 1f;
            const float target = 5f;
            const float tau = 0.15f;
            const float deltaTime = 0.1f;

            var once = GazeAim.Smooth(current, target, deltaTime, tau);

            var half = GazeAim.Smooth(current, target, deltaTime / 2f, tau);
            var twice = GazeAim.Smooth(half, target, deltaTime / 2f, tau);

            Assert.That(twice, Is.EqualTo(once).Within(1e-4f));
        }

        /// <summary><c>tau &lt;= 0</c> なら即座に目標へスナップする。</summary>
        [Test]
        public void NonPositiveTauSnapsToTarget()
        {
            Assert.That(GazeAim.Smooth(1f, 5f, 0.1f, 0f), Is.EqualTo(5f));
            Assert.That(GazeAim.Smooth(1f, 5f, 0.1f, -1f), Is.EqualTo(5f));
        }

        /// <summary><c>deltaTime &lt;= 0</c> なら現在値のまま変化しない。</summary>
        [Test]
        public void NonPositiveDeltaTimeLeavesCurrentUnchanged()
        {
            Assert.That(GazeAim.Smooth(1f, 5f, 0f, 0.15f), Is.EqualTo(1f));
            Assert.That(GazeAim.Smooth(1f, 5f, -1f, 0.15f), Is.EqualTo(1f));
        }
    }
}
