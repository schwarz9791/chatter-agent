using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class ShutdownPolicyTests
    {
        // ── ShouldDefer ────────────────────────────────────────────────

        /// <summary>
        /// ★ <b>#68 の本題。</b> 未 ack が無いのに保留していたので、
        /// <c>Application.Quit()</c> が効かない環境では「終了」を2回選ぶことになっていた。
        /// </summary>
        [Test]
        public void DoesNotDeferWhenThereIsNothingToFlush()
        {
            Assert.That(ShutdownPolicy.ShouldDefer(hasPendingWork: false, alreadyRequested: false), Is.False);
        }

        [Test]
        public void DefersOnceWhenAnAckIsStillPending()
        {
            Assert.That(ShutdownPolicy.ShouldDefer(hasPendingWork: true, alreadyRequested: false), Is.True);
        }

        /// <summary>
        /// ★ <b>2周目は必ず通すこと。</b> 保留し続けるとアプリが終了しなくなる。
        /// 投げ切れなかった ack は次回起動で二重発話になるだけで、こちらの方が軽い。
        /// </summary>
        [Test]
        public void NeverDefersTwice()
        {
            Assert.That(ShutdownPolicy.ShouldDefer(hasPendingWork: true, alreadyRequested: true), Is.False);
            Assert.That(ShutdownPolicy.ShouldDefer(hasPendingWork: false, alreadyRequested: true), Is.False);
        }

        // ── ShouldRetryQuit ────────────────────────────────────────────

        private const int MaxAttempts = 3;
        private const double IntervalSeconds = 2;

        private static bool Retry(double now, double lastAttempt, int attempts)
        {
            return ShutdownPolicy.ShouldRetryQuit(now, lastAttempt, attempts, MaxAttempts, IntervalSeconds);
        }

        [Test]
        public void DoesNotRetryBeforeTheInterval()
        {
            Assert.That(Retry(now: 10, lastAttempt: 10, attempts: 1), Is.False);
            Assert.That(Retry(now: 10 + IntervalSeconds - 0.01, lastAttempt: 10, attempts: 1), Is.False);
        }

        [Test]
        public void RetriesOnceTheIntervalHasPassed()
        {
            Assert.That(Retry(now: 10 + IntervalSeconds, lastAttempt: 10, attempts: 1), Is.True);
            Assert.That(Retry(now: 60, lastAttempt: 10, attempts: 2), Is.True);
        }

        /// <summary>
        /// ★ <b>撃ち止めがあること。</b> 無限に呼び直すと、原因が別（そもそも
        /// <c>Quit()</c> に到達していない）だったときにログが洪水になる。
        /// </summary>
        [Test]
        public void StopsAtMaxAttempts()
        {
            Assert.That(Retry(now: 100, lastAttempt: 10, attempts: MaxAttempts), Is.False);
            Assert.That(Retry(now: 100, lastAttempt: 10, attempts: MaxAttempts + 1), Is.False);
        }

        /// <summary>初回の <c>Quit()</c> を呼ぶ前（attempts=0）は再試行の出番ではない。</summary>
        [Test]
        public void DoesNotRetryBeforeTheFirstAttempt()
        {
            Assert.That(Retry(now: 100, lastAttempt: 10, attempts: 0), Is.False);
        }

        /// <summary>0 以下はキルスイッチ（<c>audioIdleSuspendMs</c> と同じ規約）。</summary>
        [Test]
        public void ZeroOrNegativeBudgetsDisableRetrying()
        {
            Assert.That(ShutdownPolicy.ShouldRetryQuit(100, 10, 1, 0, IntervalSeconds), Is.False);
            Assert.That(ShutdownPolicy.ShouldRetryQuit(100, 10, 1, -1, IntervalSeconds), Is.False);
            Assert.That(ShutdownPolicy.ShouldRetryQuit(100, 10, 1, MaxAttempts, 0), Is.False);
            Assert.That(ShutdownPolicy.ShouldRetryQuit(100, 10, 1, MaxAttempts, -1), Is.False);
        }

        /// <summary>
        /// ★ 時計が巻き戻っても撃たない（差分でしか見ない）。
        /// </summary>
        [Test]
        public void DoesNotRetryWhenTheClockWentBackwards()
        {
            Assert.That(Retry(now: 5, lastAttempt: 10, attempts: 1), Is.False);
        }
    }
}
