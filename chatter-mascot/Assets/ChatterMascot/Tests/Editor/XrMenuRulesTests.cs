using ChatterMascot.Xr;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary><see cref="XrMenuRules.ShowInvoker"/> の猶予・パネルが開いている間の扱い。</summary>
    [TestFixture]
    public sealed class XrMenuRulesTests
    {
        [Test]
        public void ShowsRightAfterAHit()
        {
            Assert.That(XrMenuRules.ShowInvoker(false, now: 10.0, lastHitAt: 10.0), Is.True);
        }

        [Test]
        public void StaysVisibleWithinTheGraceWindow()
        {
            var lastHitAt = 10.0 - (XrMenuRules.HoverGraceSeconds - 0.1);
            Assert.That(XrMenuRules.ShowInvoker(false, now: 10.0, lastHitAt: lastHitAt), Is.True);
        }

        [Test]
        public void HidesAfterTheGraceWindowElapses()
        {
            var lastHitAt = 10.0 - (XrMenuRules.HoverGraceSeconds + 0.1);
            Assert.That(XrMenuRules.ShowInvoker(false, now: 10.0, lastHitAt: lastHitAt), Is.False);
        }

        [Test]
        public void NeverShowsWhileThePanelIsOpen()
        {
            Assert.That(XrMenuRules.ShowInvoker(true, now: 10.0, lastHitAt: 10.0), Is.False);
        }

        [Test]
        public void HidesImmediatelyWhenNeverHit()
        {
            Assert.That(XrMenuRules.ShowInvoker(false, now: 10.0, lastHitAt: double.NegativeInfinity), Is.False);
        }

        [Test]
        public void RespectsACustomGraceWindow()
        {
            Assert.That(XrMenuRules.ShowInvoker(false, now: 10.0, lastHitAt: 5.0, graceSeconds: 4f), Is.False);
            Assert.That(XrMenuRules.ShowInvoker(false, now: 10.0, lastHitAt: 5.0, graceSeconds: 6f), Is.True);
        }

        [Test]
        public void GearHidesWhileHandTrackingIsAvailable()
        {
            Assert.That(XrMenuRules.ShowGear(panelOpen: false, handTrackingAvailable: true, now: 10.0, lastHitAt: 10.0), Is.False);
        }

        [Test]
        public void GearFallsBackToShowInvokerWithoutHandTracking()
        {
            Assert.That(XrMenuRules.ShowGear(panelOpen: false, handTrackingAvailable: false, now: 10.0, lastHitAt: 10.0), Is.True);
            Assert.That(XrMenuRules.ShowGear(panelOpen: true, handTrackingAvailable: false, now: 10.0, lastHitAt: 10.0), Is.False);
        }

        [Test]
        public void HandTrackingAvailableRightAfterATrackedJoint()
        {
            Assert.That(XrMenuRules.HandTrackingAvailable(now: 10.0, lastTrackedAt: 10.0), Is.True);
        }

        [Test]
        public void HandTrackingAvailableStaysWithinTheGraceWindow()
        {
            var lastTrackedAt = 10.0 - (XrMenuRules.PalmTrackingGraceSeconds - 0.1);
            Assert.That(XrMenuRules.HandTrackingAvailable(now: 10.0, lastTrackedAt: lastTrackedAt), Is.True);
        }

        [Test]
        public void HandTrackingUnavailableAfterTheGraceWindowElapses()
        {
            var lastTrackedAt = 10.0 - (XrMenuRules.PalmTrackingGraceSeconds + 0.1);
            Assert.That(XrMenuRules.HandTrackingAvailable(now: 10.0, lastTrackedAt: lastTrackedAt), Is.False);
        }

        [Test]
        public void PalmFacingEntersAboveTheEnterThreshold()
        {
            Assert.That(XrMenuRules.IsPalmFacingSelf(false, XrMenuRules.PalmFacingEnterDot + 0.01f), Is.True);
            Assert.That(XrMenuRules.IsPalmFacingSelf(false, XrMenuRules.PalmFacingEnterDot - 0.01f), Is.False);
        }

        [Test]
        public void PalmFacingStaysOnWithinTheHysteresisBand()
        {
            // 抜け閾値と入り閾値の間では、いったん向けていた判定を保つ
            var dot = (XrMenuRules.PalmFacingEnterDot + XrMenuRules.PalmFacingExitDot) / 2f;
            Assert.That(XrMenuRules.IsPalmFacingSelf(true, dot), Is.True);
            Assert.That(XrMenuRules.IsPalmFacingSelf(false, dot), Is.False);
        }

        [Test]
        public void PalmFacingExitsBelowTheExitThreshold()
        {
            Assert.That(XrMenuRules.IsPalmFacingSelf(true, XrMenuRules.PalmFacingExitDot - 0.01f), Is.False);
            Assert.That(XrMenuRules.IsPalmFacingSelf(true, XrMenuRules.PalmFacingExitDot + 0.01f), Is.True);
        }

        [Test]
        public void ShowsThePalmButtonOnlyWhenTrackedAndFacing()
        {
            Assert.That(XrMenuRules.ShowPalmButton(panelOpen: false, handTrackingAvailable: true, palmFacingSelf: true), Is.True);
            Assert.That(XrMenuRules.ShowPalmButton(panelOpen: false, handTrackingAvailable: true, palmFacingSelf: false), Is.False);
            Assert.That(XrMenuRules.ShowPalmButton(panelOpen: false, handTrackingAvailable: false, palmFacingSelf: true), Is.False);
            Assert.That(XrMenuRules.ShowPalmButton(panelOpen: true, handTrackingAvailable: true, palmFacingSelf: true), Is.False);
        }
    }
}
