using ChatterMascot.Audio;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class AudioIdleGateTests
    {
        private const long SuspendAfterMs = 5000;

        private static AudioIdleGate Gate()
        {
            return new AudioIdleGate(SuspendAfterMs);
        }

        [Test]
        public void DoesNotSuspendBeforeTheGracePeriod()
        {
            var gate = Gate();

            Assert.That(gate.Tick(1000, 0, 0), Is.EqualTo(IdleAction.None));
            Assert.That(gate.Tick(1000 + SuspendAfterMs - 1, 0, 0), Is.EqualTo(IdleAction.None));
            Assert.That(gate.IsSuspended, Is.False);
        }

        [Test]
        public void SuspendsOnceAfterTheGracePeriod()
        {
            var gate = Gate();

            gate.Tick(1000, 0, 0);
            Assert.That(gate.Tick(1000 + SuspendAfterMs, 0, 0), Is.EqualTo(IdleAction.Suspend));
            Assert.That(gate.IsSuspended, Is.True);

            // ★ 連続で出さないこと。出すと毎フレーム mixerSuspend を叩く
            Assert.That(gate.Tick(1000 + SuspendAfterMs + 1, 0, 0), Is.EqualTo(IdleAction.None));
            Assert.That(gate.Tick(999999, 0, 0), Is.EqualTo(IdleAction.None));
        }

        /// <summary>
        /// ★ <b>契約1（孤児を鳴らし切る）の直接の防衛線。</b>
        ///
        /// 鳴っている最中に手放すと、内部状態を保つ実装（FMOD の <c>mixerSuspend</c>）では
        /// <b>その音が凍る</b>。採番のやり直しで孤児になった音は
        /// <c>Items</c> から外れているので、<c>itemsInFlight</c> が 0 でも鳴っていることがある。
        /// </summary>
        [Test]
        public void NeverSuspendsWhileSomethingIsPlaying()
        {
            var gate = Gate();

            gate.Tick(1000, 1, 0);
            Assert.That(gate.Tick(1000 + SuspendAfterMs * 10, 1, 0), Is.EqualTo(IdleAction.None));
            Assert.That(gate.IsSuspended, Is.False);
        }

        /// <summary>
        /// キューに残っている間も手放さない。<c>Pending</c> / <c>Fetching</c> / <c>Ready</c> は
        /// 「合成待ちで、まもなく鳴る」状態なので、ここで手放すと掴み直しが再生に間に合わない。
        /// </summary>
        [Test]
        public void NeverSuspendsWhileItemsRemain()
        {
            var gate = Gate();

            gate.Tick(1000, 0, 3);
            Assert.That(gate.Tick(1000 + SuspendAfterMs * 10, 0, 3), Is.EqualTo(IdleAction.None));
            Assert.That(gate.IsSuspended, Is.False);
        }

        [Test]
        public void ResumesOnceWhenWorkIsAnnounced()
        {
            var gate = Gate();
            gate.Tick(1000, 0, 0);
            gate.Tick(1000 + SuspendAfterMs, 0, 0);
            Assert.That(gate.IsSuspended, Is.True);

            Assert.That(gate.NoteWorkIncoming(20000), Is.EqualTo(IdleAction.Resume));
            Assert.That(gate.IsSuspended, Is.False);

            // 掴んでいるときに告げても何も起きない（べき等）
            Assert.That(gate.NoteWorkIncoming(20001), Is.EqualTo(IdleAction.None));
        }

        /// <summary>
        /// <c>NoteWorkIncoming</c> を経ずに鳴り始めた場合でも掴み直す（保険）。
        /// </summary>
        [Test]
        public void ResumesWhenPlaybackStartsWithoutAnnouncement()
        {
            var gate = Gate();
            gate.Tick(1000, 0, 0);
            gate.Tick(1000 + SuspendAfterMs, 0, 0);
            Assert.That(gate.IsSuspended, Is.True);

            Assert.That(gate.Tick(20000, 1, 0), Is.EqualTo(IdleAction.Resume));
            Assert.That(gate.IsSuspended, Is.False);
        }

        /// <summary>
        /// 仕事が入って静かになったら、猶予は<b>そこから測り直す</b>。
        /// </summary>
        [Test]
        public void RestartsTheGracePeriodAfterWork()
        {
            var gate = Gate();

            gate.Tick(1000, 0, 0);
            // 4秒目に仕事が入る（猶予は 5秒）
            gate.Tick(5000, 1, 0);
            // 仕事が終わってから改めて 5秒経つまでは手放さない
            gate.Tick(6000, 0, 0);
            Assert.That(gate.Tick(6000 + SuspendAfterMs - 1, 0, 0), Is.EqualTo(IdleAction.None));
            Assert.That(gate.Tick(6000 + SuspendAfterMs, 0, 0), Is.EqualTo(IdleAction.Suspend));
        }

        /// <summary>キルスイッチ。掴み直してから以後は何もしない。</summary>
        [Test]
        public void DisablingWakesUpAndStaysQuiet()
        {
            var gate = Gate();
            gate.Tick(1000, 0, 0);
            gate.Tick(1000 + SuspendAfterMs, 0, 0);
            Assert.That(gate.IsSuspended, Is.True);

            gate.Enabled = false;
            Assert.That(gate.Tick(20000, 0, 0), Is.EqualTo(IdleAction.Resume));
            Assert.That(gate.Tick(20001, 0, 0), Is.EqualTo(IdleAction.None));
            Assert.That(gate.Tick(999999, 0, 0), Is.EqualTo(IdleAction.None));
            Assert.That(gate.IsSuspended, Is.False);
        }

        [Test]
        public void DisabledGateNeverSuspends()
        {
            var gate = Gate();
            gate.Enabled = false;

            gate.Tick(1000, 0, 0);
            Assert.That(gate.Tick(1000 + SuspendAfterMs * 10, 0, 0), Is.EqualTo(IdleAction.None));
            Assert.That(gate.IsSuspended, Is.False);
        }
    }
}
