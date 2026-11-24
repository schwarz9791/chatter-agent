using ChatterMascot.Xr;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="XrMouseActivity"/> の「触っていない」判定。ワールドと頭の両方の向きが変わったときだけ
    /// 動かしたとみなすこと、押下、追跡の途切れ。
    /// </summary>
    [TestFixture]
    public sealed class XrMouseActivityTests
    {
        private static readonly Vector3 Forward = Vector3.forward;
        private static readonly Vector3 Turned = Quaternion.Euler(0f, XrMouseActivity.MoveDegrees * 5f, 0f) * Vector3.forward;
        private static readonly Vector3 BarelyTurned = Quaternion.Euler(0f, XrMouseActivity.MoveDegrees * 0.5f, 0f) * Vector3.forward;

        [Test]
        public void FreshInstanceIsIdle()
        {
            Assert.That(new XrMouseActivity().IsIdle(0), Is.True);
        }

        [Test]
        public void FirstTrackedFrameOnlyTakesTheReference()
        {
            var activity = new XrMouseActivity();
            activity.Update(true, Forward, Forward, false, 10);
            Assert.That(activity.IsIdle(10), Is.True);
        }

        [Test]
        public void TurningOnlyTheHeadFrameDirectionStaysIdle()
        {
            var activity = new XrMouseActivity();
            activity.Update(true, Forward, Forward, false, 10);
            activity.Update(true, Forward, Turned, false, 11);
            Assert.That(activity.IsIdle(11), Is.True);
        }

        [Test]
        public void TurningOnlyTheWorldDirectionStaysIdle()
        {
            var activity = new XrMouseActivity();
            activity.Update(true, Forward, Forward, false, 10);
            activity.Update(true, Turned, Forward, false, 11);
            Assert.That(activity.IsIdle(11), Is.True);
        }

        [Test]
        public void ChangingBothBecomesActiveAndGoesIdleAgainAfterTheTimeout()
        {
            var activity = new XrMouseActivity();
            activity.Update(true, Forward, Forward, false, 10);
            activity.Update(true, Turned, Turned, false, 11);
            Assert.That(activity.IsIdle(11), Is.False);
            Assert.That(activity.IsIdle(11 + XrMouseActivity.IdleSeconds - 0.01), Is.False);
            Assert.That(activity.IsIdle(11 + XrMouseActivity.IdleSeconds + 0.01), Is.True);
        }

        [Test]
        public void ChangeBelowTheThresholdInBothStaysIdle()
        {
            var activity = new XrMouseActivity();
            activity.Update(true, Forward, Forward, false, 10);
            activity.Update(true, BarelyTurned, BarelyTurned, false, 11);
            Assert.That(activity.IsIdle(11), Is.True);
        }

        [Test]
        public void PressingMakesItActiveWithoutMovement()
        {
            var activity = new XrMouseActivity();
            activity.Update(true, Forward, Forward, false, 10);
            activity.Update(true, Forward, Forward, true, 11);
            Assert.That(activity.IsIdle(11), Is.False);
        }

        [Test]
        public void LosingTrackingAndReturningWithADifferentDirectionIsNotATouch()
        {
            var activity = new XrMouseActivity();
            activity.Update(true, Forward, Forward, false, 10);
            activity.Update(false, Forward, Forward, false, 11);
            activity.Update(true, Turned, Turned, false, 12);
            Assert.That(activity.IsIdle(12), Is.True);
        }
    }
}
