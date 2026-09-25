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
            var lastHitAt = 10.0 - (XrMenuRules.GearVisibleSeconds - 0.1);
            Assert.That(XrMenuRules.ShowInvoker(false, now: 10.0, lastHitAt: lastHitAt), Is.True);
        }

        [Test]
        public void HidesAfterTheGraceWindowElapses()
        {
            var lastHitAt = 10.0 - (XrMenuRules.GearVisibleSeconds + 0.1);
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
        public void PanelFollowsOnlyAfterLeavingTheGazeWidely()
        {
            Assert.That(XrMenuRules.ShouldFollowPanel(false, XrMenuRules.PanelFollowStartDegrees - 1f), Is.False);
            Assert.That(XrMenuRules.ShouldFollowPanel(false, XrMenuRules.PanelFollowStartDegrees + 1f), Is.True);
        }

        [Test]
        public void PanelKeepsFollowingUntilItIsBackInFront()
        {
            var between = (XrMenuRules.PanelFollowStartDegrees + XrMenuRules.PanelFollowStopDegrees) / 2f;
            Assert.That(XrMenuRules.ShouldFollowPanel(true, between), Is.True);
            Assert.That(XrMenuRules.ShouldFollowPanel(true, XrMenuRules.PanelFollowStopDegrees - 1f), Is.False);
        }

        [Test]
        public void ShowsThePalmButtonOnlyWhenTrackedAndFacing()
        {
            Assert.That(XrMenuRules.ShowPalmButton(panelOpen: false, handTrackingAvailable: true, palmFacingSelf: true), Is.True);
            Assert.That(XrMenuRules.ShowPalmButton(panelOpen: false, handTrackingAvailable: true, palmFacingSelf: false), Is.False);
            Assert.That(XrMenuRules.ShowPalmButton(panelOpen: false, handTrackingAvailable: false, palmFacingSelf: true), Is.False);
            Assert.That(XrMenuRules.ShowPalmButton(panelOpen: true, handTrackingAvailable: true, palmFacingSelf: true), Is.False);
        }

        [Test]
        public void GearScaleIsOneAtTheReferenceHeight()
        {
            Assert.That(XrMenuRules.GearScale(XrMenuRules.GearReferenceHeightMeters), Is.EqualTo(1f));
        }

        [Test]
        public void GearScaleIsMonotonicNonDecreasingWithHeight()
        {
            var previous = XrMenuRules.GearScale(0.15f);
            for (var h = 0.2f; h <= 3.0f; h += 0.1f)
            {
                var current = XrMenuRules.GearScale(h);
                Assert.That(current, Is.GreaterThanOrEqualTo(previous));
                previous = current;
            }
        }

        [Test]
        public void GearScaleStaysWithinItsBounds()
        {
            foreach (var h in new[] { 0.01f, 0.15f, XrMenuRules.GearReferenceHeightMeters, 1f, 2f, 100f })
            {
                var scale = XrMenuRules.GearScale(h);
                Assert.That(scale, Is.GreaterThanOrEqualTo(1f));
                Assert.That(scale, Is.LessThanOrEqualTo(XrMenuRules.GearScaleMax));
            }
        }

        [Test]
        public void GearScaleFallsBackToOneForNonPositiveOrNonFiniteInput()
        {
            Assert.That(XrMenuRules.GearScale(0f), Is.EqualTo(1f));
            Assert.That(XrMenuRules.GearScale(-1f), Is.EqualTo(1f));
            Assert.That(XrMenuRules.GearScale(float.NaN), Is.EqualTo(1f));
            Assert.That(XrMenuRules.GearScale(float.PositiveInfinity), Is.EqualTo(1f));
        }
    }
}
