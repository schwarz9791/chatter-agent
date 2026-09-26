using System;
using ChatterMascot.Xr;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="Wander"/> の速度式・目的地の抽選・円のクランプ・状態機械。
    /// </summary>
    [TestFixture]
    public sealed class WanderTests
    {
        private const float Tolerance = 1e-4f;
        private static readonly Vector2 Origin = Vector2.zero;
        private static readonly Vector2 CameraXz = new Vector2(0f, 1f);

        /// <summary>待ちが必ず明けている時刻。<see cref="Wander.MaxRestSeconds"/> に追従させる。</summary>
        private static readonly double AfterRest = Wander.MaxRestSeconds + 1.0;

        /// <summary>指定した値を順に返し、尽きたら最後の値を返し続ける乱数源。</summary>
        private static Func<double> Sequence(params double[] values)
        {
            var i = 0;
            return () => values[Math.Min(i++, values.Length - 1)];
        }

        /// <summary>
        /// 歩行フェーズが終わる（到着か <see cref="Wander.MaxWalkSeconds"/> の打ち切り）まで、
        /// 実機のフレーム刻みに近い <c>dt = 1/72</c> で <see cref="Wander.Update"/> を積む。
        /// 1フレームの巨大な speed で瞬時に着かせる代わりに、実際に収束することを確かめる。
        /// </summary>
        /// <returns>ループを抜けた時点の <c>now</c>。打ち切りと区別する判定に使う。</returns>
        private static double WalkUntilStopped(Wander wander, double startNow, float radius, float speed)
        {
            const float dt = 1f / 72f;
            var now = startNow;
            while (wander.Phase == WanderPhase.Walking && now - startNow < Wander.MaxWalkSeconds)
            {
                now += dt;
                wander.Update(now, false, true, Origin, radius, speed, CameraXz, () => 0.5);
            }
            return now;
        }

        /// <summary>
        /// 抽選が済んで歩き出した状態。目的地は円の縁 <c>(0, radius)</c>、そこを向くヨーは 180 度。
        /// </summary>
        private static Wander WalkingWander(float radius, float speed)
        {
            var wander = new Wander(Origin, 180f);
            var random = Sequence(0.0, 0.0, 1.0);

            wander.Update(0.0, false, true, Origin, radius, speed, CameraXz, random);
            wander.Update(AfterRest, false, true, Origin, radius, speed, CameraXz, random);
            return wander;
        }

        // ---- 速度 ----

        [TestCase(0.53f, 2, 1.3f, 1f)]
        [TestCase(0.53f, 2, 1.3f, 0.18f)]
        [TestCase(1f, 4, 2f, 1f)]
        public void SpeedMatchesStrideTimesStepsDividedByCycleTimesScale(float stride, int steps, float cycle, float scale)
        {
            var expected = stride * steps / cycle * scale;
            Assert.That(Wander.Speed(stride, steps, cycle, scale), Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void SpeedIsZeroWhenStrideIsZero()
        {
            Assert.That(Wander.Speed(0f, 2, 1.3f, 1f), Is.EqualTo(0f));
        }

        [TestCase(0f)]
        [TestCase(-1f)]
        [TestCase(float.NaN)]
        public void SpeedIsZeroWhenCycleIsNotPositive(float cycleSeconds)
        {
            Assert.That(Wander.Speed(0.5f, 2, cycleSeconds, 1f), Is.EqualTo(0f));
        }

        // ---- 歩行範囲の半径の倍率 ----

        [TestCase(0.25f, 1f)]
        [TestCase(0.5f, 2f)]
        [TestCase(1.6f, 6.4f)]
        public void AreaScaleIsProportionalToHeight(float heightMeters, float expected)
        {
            Assert.That(Wander.AreaScale(heightMeters), Is.EqualTo(expected).Within(Tolerance));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(0f)]
        [TestCase(-1f)]
        public void AreaScaleIsOneForNonFiniteOrNonPositiveHeight(float heightMeters)
        {
            Assert.That(Wander.AreaScale(heightMeters), Is.EqualTo(1f));
        }

        // ---- 目的地の抽選 ----

        [TestCase(0.0, 0.0)]
        [TestCase(0.0, 0.999999)]
        [TestCase(0.999999, 0.0)]
        [TestCase(0.999999, 0.999999)]
        [TestCase(0.5, 0.5)]
        [TestCase(1.0, 1.0)]
        public void PickDestinationIsAlwaysInsideTheCircle(double angleFraction, double radiusFraction)
        {
            var center = new Vector2(1.5f, -2.5f);
            const float radius = 0.3f;

            var destination = Wander.PickDestination(center, radius, Sequence(angleFraction, radiusFraction));

            Assert.That(Vector2.Distance(destination, center), Is.LessThanOrEqualTo(radius + Tolerance));
        }

        // ---- 円のクランプ（Advance） ----

        [Test]
        public void AdvanceDiscardsAnOutwardStepWhenStartingOutsideTheCircle()
        {
            var center = Origin;
            const float radius = 0.2f;
            var from = new Vector2(0.5f, 0f);

            // 外へさらに出ようとする一歩は丸ごと捨てて from のまま
            var outward = Wander.Advance(from, new Vector2(1f, 0f), center, radius);
            Assert.That(outward, Is.EqualTo(from));

            // 中心へ戻る一歩は通る
            var inward = Wander.Advance(from, Vector2.zero, center, radius);
            Assert.That(inward, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void AdvanceDiscardsAStepThatLeavesTheCircleWhenStartingInside()
        {
            var center = Origin;
            const float radius = 0.2f;
            var from = new Vector2(0.1f, 0f);

            var moved = Wander.Advance(from, new Vector2(10f, 0f), center, radius);

            Assert.That(moved, Is.EqualTo(from));
        }

        [Test]
        public void AdvanceReachesTheTargetWhenItStaysInsideTheCircle()
        {
            var target = new Vector2(0.05f, 0f);
            var moved = Wander.Advance(Vector2.zero, target, Origin, 0.2f);

            Assert.That(moved, Is.EqualTo(target));
        }

        // ---- 状態機械 ----

        [Test]
        public void DoesNotMoveWhileSpeaking()
        {
            var wander = new Wander(new Vector2(0.05f, -0.05f), 12f);
            var before = wander.Position;

            for (var i = 0; i < 5; i++)
            {
                wander.Update(i * 1.0, true, true, Origin, 0.2f, 1f, CameraXz, () => 0.5);
            }

            Assert.That(wander.Position, Is.EqualTo(before));
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Resting));
        }

        [Test]
        public void DoesNotMoveWhileCanWalkIsFalse()
        {
            var wander = new Wander(new Vector2(0.1f, 0.2f), 30f);
            var before = wander.Position;

            for (var i = 0; i < 5; i++)
            {
                wander.Update(i * 100.0, false, false, Origin, 0.2f, 1f, CameraXz, () => 0.5);
            }

            Assert.That(wander.Position, Is.EqualTo(before));
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Resting));
        }

        [Test]
        public void DoesNotWanderBeforeTheRestTimerElapses()
        {
            var wander = new Wander(Origin, 0f);
            var random = Sequence(1.0); // 待ち = MaxRestSeconds

            wander.Update(0.0, false, true, Origin, 0.2f, 1f, CameraXz, random);
            wander.Update(10.0, false, true, Origin, 0.2f, 1f, CameraXz, random);

            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Resting));
            Assert.That(wander.Position, Is.EqualTo(Origin));
        }

        [Test]
        public void RedrawsTheDestinationWhenTheFirstDrawIsTooShort()
        {
            // 1回目の抽選（angle=0, radius=0）は現在地そのもの。2回目（angle=0, radius=1）で縁まで離れる
            var wander = new Wander(Origin, 0f);
            var random = Sequence(0.0, 0.0, 0.0, 0.0, 1.0);

            wander.Update(0.0, false, true, Origin, 0.2f, 1f, CameraXz, random); // arm
            wander.Update(AfterRest, false, true, Origin, 0.2f, 1f, CameraXz, random);

            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Walking));
        }

        [Test]
        public void WaitsAgainWhenEveryDrawIsTooShort()
        {
            // 乱数を 0 に固定すると、何度引いても目的地は現在地のまま → 歩き出さず待ちに戻る
            var wander = new Wander(Origin, 0f);

            wander.Update(0.0, false, true, Origin, 0.2f, 1f, CameraXz, () => 0.0); // arm
            wander.Update(AfterRest, false, true, Origin, 0.2f, 1f, CameraXz, () => 0.0);

            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Resting));
            Assert.That(wander.Position, Is.EqualTo(Origin));
        }

        [Test]
        public void TheMinimumTravelIsCappedByTheRadiusSoAFastWalkStillLeaves()
        {
            // 速度が大きくても下限は半径で頭打ちになるので、円の縁への移動は必ず通る
            var wander = new Wander(Origin, 0f);
            var random = Sequence(0.0, 0.0, 1.0);

            wander.Update(0.0, false, true, Origin, 0.2f, 100f, CameraXz, random);
            wander.Update(AfterRest, false, true, Origin, 0.2f, 100f, CameraXz, random);

            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Walking));
        }

        [Test]
        public void WalksAlongItsFacingRatherThanStraightAtTheDestination()
        {
            // 目的地は真横（(-radius, 0)）。目的地へ直接寄せていたら y は動かないはず
            // —— 正面（初期ヨー 0 = −z）へ進むので、最初の一歩は y が負に振れる
            var wander = new Wander(Origin, 0f);
            wander.GoTo(0.0, new Vector2(-0.2f, 0f));

            wander.Update(0.0, false, true, Origin, 0.2f, 0.1f, CameraXz, () => 0.5);
            wander.Update(0.1, false, true, Origin, 0.2f, 0.1f, CameraXz, () => 0.5);

            Assert.That(wander.Position.y, Is.LessThan(0f));
        }

        [Test]
        public void GoToStartsWalkingTowardTheGivenDestinationEvenIfItIsShort()
        {
            // 指された移動には「短すぎる移動は見送る」規則を掛けない
            var wander = new Wander(Origin, 180f);
            // 5cm は抽選なら見送られる長さ（下限は半径の半分＝10cm）
            var target = new Vector2(0f, 0.05f);

            wander.GoTo(0.0, target);
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Walking));

            WalkUntilStopped(wander, 0.0, 0.2f, 1f);

            Assert.That(Vector2.Distance(wander.Position, target), Is.LessThanOrEqualTo(Wander.ArrivalMeters));
            Assert.That(wander.Phase, Is.Not.EqualTo(WanderPhase.Walking));
        }

        [Test]
        public void GoToDoesNotMoveWhileSpeaking()
        {
            var wander = new Wander(Origin, 180f);

            wander.GoTo(0.0, new Vector2(0f, 0.15f));
            wander.Update(0.0, true, true, Origin, 0.2f, 1f, CameraXz, () => 0.5);
            wander.Update(0.1, true, true, Origin, 0.2f, 1f, CameraXz, () => 0.5);

            Assert.That(wander.Phase, Is.Not.EqualTo(WanderPhase.Walking));
            Assert.That(wander.Position, Is.EqualTo(Origin));
        }

        [Test]
        public void ForwardPointsAlongLocalMinusZ()
        {
            Assert.That(Wander.Forward(0f).y, Is.EqualTo(-1f).Within(Tolerance));
            Assert.That(Wander.Forward(180f).y, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(Wander.Forward(90f).x, Is.EqualTo(-1f).Within(Tolerance));
        }

        [Test]
        public void StopsWalkingImmediatelyWhenSpeakingStarts()
        {
            var wander = WalkingWander(0.2f, 1f);
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Walking));
            var positionAtInterrupt = wander.Position;

            wander.Update(AfterRest + 0.1, true, true, Origin, 0.2f, 1f, CameraXz, () => 0.5);

            // 既にカメラを向いているので向き直りはその場で終わる。見るのは
            // 「歩行をやめたか」と「動かなかったか」
            Assert.That(wander.Phase, Is.Not.EqualTo(WanderPhase.Walking));
            Assert.That(wander.Position, Is.EqualTo(positionAtInterrupt));
        }

        [Test]
        public void GivesUpAfterTheMaxWalkDurationEvenWithoutArriving()
        {
            var wander = WalkingWander(0.2f, 1f);
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Walking));

            // 歩行の上限（12秒）を超えたら、到着していなくても打ち切る
            wander.Update(AfterRest + Wander.MaxWalkSeconds + 0.1, false, true, Origin, 0.2f, 0f, CameraXz, () => 0.5);

            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Facing));
        }

        [Test]
        public void ArrivesAndSwitchesToFacingWithinTheArrivalTolerance()
        {
            var wander = WalkingWander(0.2f, 1f);
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Walking));

            var stoppedAt = WalkUntilStopped(wander, AfterRest, 0.2f, 1f);

            Assert.That(stoppedAt - AfterRest, Is.LessThan(Wander.MaxWalkSeconds));
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Facing));
            Assert.That(Vector2.Distance(wander.Position, new Vector2(0f, 0.2f)), Is.LessThanOrEqualTo(Wander.ArrivalMeters));
        }

        [Test]
        public void ArrivesAtANearSideTarget()
        {
            // モデルの縮尺・実際の歩行クリップに合わせた既定速度で、正面から少し外れた
            // 近い目的地にも到着できること（旋回の頭打ちで限界サイクルに入らないことの確認）
            var speed = Wander.Speed(0.53f, 2, 1.30f, 0.18f);
            var wander = new Wander(Origin, 0f);
            var target = new Vector2(0.04f, 0f);

            wander.GoTo(0.0, target);
            var stoppedAt = WalkUntilStopped(wander, 0.0, 0.2f, speed);

            Assert.That(stoppedAt, Is.LessThan(Wander.MaxWalkSeconds));
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Facing));
        }

        [Test]
        public void ReachesEveryTargetInsideASmallCircle()
        {
            // 半径いっぱいに広く散らした始点・向き・目的地のどれでも、限界サイクルに落ちずに
            // 到着すること（打ち切りでないこと）を確かめる
            const float radius = 0.10f;
            var speed = Wander.Speed(0.53f, 2, 1.30f, 0.18f);
            var random = new System.Random(20260923);

            for (var i = 0; i < 300; i++)
            {
                var start = RandomPointInCircle(radius, random);
                var startYaw = (float)(random.NextDouble() * 360.0);
                var target = RandomPointInCircle(radius, random);

                var wander = new Wander(start, startYaw);
                wander.GoTo(0.0, target);
                var stoppedAt = WalkUntilStopped(wander, 0.0, radius, speed);

                Assert.That(stoppedAt, Is.LessThan(Wander.MaxWalkSeconds), $"case {i}");
                Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Facing), $"case {i}");
            }
        }

        private static Vector2 RandomPointInCircle(float radius, System.Random random)
        {
            var angle = (float)(random.NextDouble() * (Math.PI * 2.0));
            var r = radius * Mathf.Sqrt((float)random.NextDouble());
            return new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * r;
        }

        [Test]
        public void NeverSlidesSidewaysAtTheRimWhenTheTargetIsBehind()
        {
            const float radius = 0.2f;
            var start = new Vector2(0f, radius);
            // 縁で外向き（Forward(180) = (0,1) = 中心の逆）。目的地は中心＝正面の逆＝背後
            var wander = new Wander(start, 180f);
            wander.GoTo(0.0, Origin);

            wander.Update(0.0, false, true, Origin, radius, 1f, CameraXz, () => 0.5);
            var before = wander.Position;

            wander.Update(1.0, false, true, Origin, radius, 1f, CameraXz, () => 0.5);

            var displacement = wander.Position - before;
            var forward = Wander.Forward(wander.YawDegrees);
            var cross = displacement.x * forward.y - displacement.y * forward.x;
            var dot = Vector2.Dot(displacement, forward);

            // 一歩は「動かない」か「正面へ動く」だけ —— 横へは滑らない
            Assert.That(Mathf.Abs(cross), Is.LessThanOrEqualTo(Tolerance));
            Assert.That(dot, Is.GreaterThanOrEqualTo(-Tolerance));
            Assert.That(Vector2.Distance(wander.Position, Origin), Is.LessThanOrEqualTo(radius + Tolerance));
        }
    }
}
