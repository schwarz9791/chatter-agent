using System;
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
                maxHeadPitchDegrees: 25f, maxHeadYawDegrees: 35f,
                eyeReachMeters: 0.6f, followSeconds: 0.15f);

            var s = Sample(0.0, p, SpeechKind.Assistant, new Vector2(1f, 1f));

            Assert.That(s.TargetLocalPosition.magnitude, Is.LessThanOrEqualTo(p.EyeReachMeters + 1e-4f));
            // 方向は保たれる（clamp で符号が反転しないこと）
            Assert.That(s.TargetLocalPosition.x, Is.GreaterThan(0f));
            Assert.That(s.TargetLocalPosition.y, Is.GreaterThan(0f));
        }

        /// <summary>頭の角度は ±<c>MaxHeadPitchDegrees</c> / ±<c>MaxHeadYawDegrees</c> を超えない。</summary>
        [Test]
        public void ClampsHeadAnglesToTheirMax()
        {
            var p = new GazeParams(
                wanderSecondsX: 5.3f, wanderSecondsY: 8.7f,
                wanderMetersX: 0.25f, wanderMetersY: 0.15f,
                eyeSensitivity: 0.4f, headSensitivity: 10f,
                maxHeadPitchDegrees: 25f, maxHeadYawDegrees: 35f,
                eyeReachMeters: 0.6f, followSeconds: 0.15f);

            var s = Sample(0.0, p, SpeechKind.Assistant, new Vector2(1f, 1f));

            Assert.That(s.HeadPitchDegrees, Is.EqualTo(p.MaxHeadPitchDegrees));
            Assert.That(s.HeadYawDegrees, Is.EqualTo(p.MaxHeadYawDegrees));
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
                maxHeadPitchDegrees: 0f, maxHeadYawDegrees: 0f,
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

        /// <summary>
        /// このアプリは常駐する（デスクトップに出しっぱなしにするのが用途そのもの）ので、
        /// <c>now</c>（<c>Time.realtimeSinceStartupAsDouble</c>）は日単位まで伸びる。
        ///
        /// ★ <c>Phase</c> が周期で畳まずに <c>now</c> を直接 float 化すると、この規模の
        ///   <c>now</c> では <c>Mathf.Sin</c> に渡る引数そのものが float の刻み幅
        ///   （数十分の一 rad）で丸められ、出力が「その時刻での正しい sin 値」から
        ///   大きくずれる（実測: 30日相当で sin 値の誤差が 0.01〜0.02。カーソルが無いときの
        ///   漂いが対象なので、症状としては「何日か点けっぱなしにすると視線の漂いが
        ///   カクつく／止まって見える」）。
        ///
        /// ★ <b><see cref="IsContinuousOverASmallTimeStep"/> と同じ「隣接フレームの差分が
        ///   ほぼ 0」という形ではこの不具合を検出できない。</b> あちらは <c>dt = 1e-6</c> を
        ///   使うが、この規模の <c>now</c> では float の刻み幅（〜0.06 rad）が
        ///   <c>dt = 1e-6</c> 相当の真の位相差（〜1e-6 rad）よりずっと大きいため、
        ///   壊れていても差分はやはり 0 に潰れて見分けが付かない（実測で確認済み）。
        ///   ここでは実際のフレーム間隔に近い <c>dt = 1/30秒</c> を使い、各時刻の値を
        ///   <b>二重精度で計算した sin の真値</b>と直接突き合わせる
        ///   （畳んで float 化していれば真値と一致し続けるはず）。
        /// </summary>
        [Test]
        public void StaysAccurateAfterWeeksOfUptime()
        {
            var p = GazeParams.Default;

            // ★ 症状は7日程度でも実測されているが、成分によっては特定の日数で
            //   たまたま誤差が小さく出ることがある（周期の整数倍に近いなど）ので、
            //   数値マージンを確実に取れる 30日を使う。
            const double thirtyDays = 30.0 * 86400.0;
            const double dt = 1.0 / 30.0;

            foreach (var t in new[] { thirtyDays, thirtyDays + dt })
            {
                var s = Sample(t, p, SpeechKind.Assistant, null);

                // 二重精度のまま計算した「真値」。float へ落とすのは最後の1回だけ。
                var wanderXTrue = (float)(Math.Sin(2.0 * Math.PI * t / p.WanderSecondsX) * p.WanderMetersX);
                var wanderYTrue = (float)(Math.Sin(2.0 * Math.PI * t / p.WanderSecondsY) * p.WanderMetersY);

                Assert.That(s.TargetLocalPosition.x, Is.EqualTo(wanderXTrue).Within(1e-3f));
                Assert.That(s.TargetLocalPosition.y, Is.EqualTo(wanderYTrue).Within(1e-3f));
            }
        }
    }
}
