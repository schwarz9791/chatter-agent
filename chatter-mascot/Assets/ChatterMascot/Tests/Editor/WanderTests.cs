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
        public void AdvanceNeverIncreasesDistanceFromCenterWhenStartingOutsideTheCircle()
        {
            var center = Origin;
            const float radius = 0.2f;
            var from = new Vector2(0.5f, 0f);
            var beforeDistance = Vector2.Distance(from, center);

            // 外へさらに出ようとする方向は、進む前の距離を超えない
            var outward = Wander.Advance(from, new Vector2(1f, 0f), 0.3f, center, radius);
            Assert.That(Vector2.Distance(outward, center), Is.LessThanOrEqualTo(beforeDistance + Tolerance));

            // 中心へ戻る方向は通る
            var inward = Wander.Advance(from, Vector2.zero, 0.3f, center, radius);
            Assert.That(Vector2.Distance(inward, center), Is.LessThan(beforeDistance));
        }

        [Test]
        public void AdvanceClampsToTheRadiusWhenStartingInsideTheCircle()
        {
            var center = Origin;
            const float radius = 0.2f;
            var from = new Vector2(0.1f, 0f);

            var moved = Wander.Advance(from, new Vector2(10f, 0f), 5f, center, radius);

            Assert.That(Vector2.Distance(moved, center), Is.EqualTo(radius).Within(Tolerance));
        }

        [Test]
        public void AdvanceReachesTheTargetWhenTheStepCoversTheDistance()
        {
            var target = new Vector2(0.05f, 0f);
            var moved = Wander.Advance(Vector2.zero, target, 10f, Origin, 0.2f);

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
            var random = Sequence(1.0); // 待ち = MaxRestSeconds（45秒）

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
            // 目的地（円の縁 (0, radius)）とは逆を向いた状態から歩き出すと、最初の一歩は
            // 正面（−z）へ出る —— 目的地へ直接寄せていたら +z へ動くはず
            var wander = new Wander(Origin, 0f);
            var random = Sequence(0.0, 0.0, 1.0);

            wander.Update(0.0, false, true, Origin, 0.2f, 0.1f, CameraXz, random);
            wander.Update(AfterRest, false, true, Origin, 0.2f, 0.1f, CameraXz, random);
            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Walking));

            wander.Update(AfterRest + 0.1, false, true, Origin, 0.2f, 0.1f, CameraXz, random);

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

            wander.Update(0.0, false, true, Origin, 0.2f, 1f, CameraXz, () => 0.5);
            wander.Update(0.1, false, true, Origin, 0.2f, 1f, CameraXz, () => 0.5);

            Assert.That(Vector2.Distance(wander.Position, target), Is.LessThanOrEqualTo(Tolerance));
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

            // 十分な速度で1フレームのうちに到着させる
            wander.Update(AfterRest + 0.1, false, true, Origin, 0.2f, 100f, CameraXz, () => 0.5);

            Assert.That(wander.Phase, Is.EqualTo(WanderPhase.Facing));
            Assert.That(Vector2.Distance(wander.Position, new Vector2(0f, 0.2f)), Is.LessThanOrEqualTo(Wander.ArrivalMeters));
        }
    }
}
