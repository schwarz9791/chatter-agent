using System.Collections.Generic;
using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="IdleAccentTimer"/>。乱数と時計を注入する純粋な状態機械なので、
    /// <see cref="BlinkTimer"/> と同じやり方で決定的に固定できる。
    /// </summary>
    [TestFixture]
    public sealed class IdleAccentTimerTests
    {
        private static MotionParams Params(double min, double max)
        {
            return new MotionParams(fadeSeconds: 0.5f, cooldownSeconds: 5.0, accentMinSeconds: min, accentMaxSeconds: max);
        }

        [Test]
        public void StaysFalseWhileSpeaking()
        {
            var timer = new IdleAccentTimer(() => 0.0, Params(1.0, 1.0));

            Assert.That(timer.ShouldFire(0.0, speaking: true), Is.False);
            Assert.That(timer.ShouldFire(100.0, speaking: true), Is.False, "どれだけ経っても発話中は出ない");
        }

        [Test]
        public void FalseBeforeTheIntervalElapses()
        {
            var timer = new IdleAccentTimer(() => 0.0, Params(10.0, 10.0));

            // 最初の呼び出しの now が起点になる
            Assert.That(timer.ShouldFire(0.0, speaking: false), Is.False);
            Assert.That(timer.ShouldFire(9.99, speaking: false), Is.False);
        }

        [Test]
        public void TrueOnceTheIntervalElapses()
        {
            var timer = new IdleAccentTimer(() => 0.0, Params(10.0, 10.0));

            Assert.That(timer.ShouldFire(0.0, speaking: false), Is.False);
            Assert.That(timer.ShouldFire(10.0, speaking: false), Is.True);
        }

        /// <summary>
        /// ★ <b>起動直後には出ない。</b> コンストラクタでは時計を読まず、最初の
        ///   <see cref="IdleAccentTimer.ShouldFire"/> の <c>now</c> を起点にする
        ///   （<see cref="BlinkTimer"/> と同じ理由）。
        /// </summary>
        [Test]
        public void DoesNotFireImmediatelyAfterConstruction()
        {
            var defaultParams = MotionParams.Default; // Min=30, Max=60
            var timer = new IdleAccentTimer(() => 0.0, defaultParams);

            Assert.That(timer.ShouldFire(0.0, speaking: false), Is.False);
            Assert.That(timer.ShouldFire(29.99, speaking: false), Is.False, "既定の下限 30秒 未満");
        }

        /// <summary>★ 発火のたびに次の間隔を乱数で引き直す。乱数を差し替えて確かめる。</summary>
        [Test]
        public void RedrawsTheIntervalEachTimeItFires()
        {
            var randoms = new Queue<double>(new[] { 0.0, 1.0 - 1e-9 });
            var timer = new IdleAccentTimer(() => randoms.Dequeue(), Params(10.0, 20.0));

            Assert.That(timer.ShouldFire(0.0, speaking: false), Is.False); // 起点で 1本目の乱数を消費
            Assert.That(timer.NextIntervalSeconds, Is.EqualTo(10.0).Within(1e-6), "random=0 → 下限");

            Assert.That(timer.ShouldFire(10.0, speaking: false), Is.True); // 発火して 2本目の乱数を消費
            Assert.That(timer.NextIntervalSeconds, Is.EqualTo(20.0).Within(1e-3), "random≈1 → 上限");
        }

        /// <summary>
        /// ★★ <b>発話の立ち下がりで起点が動く。</b> 喋り終わった直後に、元の起点基準では
        ///   もう間隔を過ぎているはずでも出ないこと。
        /// </summary>
        [Test]
        public void FallingEdgeOfSpeechMovesTheStartingPoint()
        {
            var timer = new IdleAccentTimer(() => 0.0, Params(10.0, 10.0));

            Assert.That(timer.ShouldFire(0.0, speaking: false), Is.False); // 起点 0.0、間隔 10

            Assert.That(timer.ShouldFire(5.0, speaking: true), Is.False);
            Assert.That(timer.ShouldFire(8.0, speaking: true), Is.False);

            // 立ち下がり。起点が 8.0 に動く
            Assert.That(timer.ShouldFire(8.0, speaking: false), Is.False);

            // リセットされていなければ元の起点(0.0)から 10秒後の 10.0 で既に true になっているはず
            Assert.That(timer.ShouldFire(17.99, speaking: false), Is.False, "起点が動いていなければここで true になってしまう");
            Assert.That(timer.ShouldFire(18.0, speaking: false), Is.True, "8.0 から10秒後");
        }
    }
}
