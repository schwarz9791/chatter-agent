using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="ClipEnd"/>。クリップ長を越えた評価を止める閾値の計算を固定する（#103）。
    /// </summary>
    [TestFixture]
    public sealed class ClipEndTests
    {
        [Test]
        public void LimitIsMarginBeforeTheEnd()
        {
            Assert.That(ClipEnd.Limit(4.017f), Is.EqualTo(4.016f).Within(1e-5f));
        }

        [Test]
        public void OvershootsPastTheLimit()
        {
            Assert.That(ClipEnd.Overshoots(4.019f, 4.017f), Is.True);
        }

        [Test]
        public void DoesNotOvershootWithinTheClip()
        {
            Assert.That(ClipEnd.Overshoots(4.000f, 4.017f), Is.False);
        }

        /// <summary>負の長さに倒れない（0 に留める）。</summary>
        [Test]
        public void ZeroLengthClampsToZero()
        {
            Assert.That(ClipEnd.Limit(0f), Is.EqualTo(0f));
        }

        [Test]
        public void WrapIsPeriodic()
        {
            const float length = 4.017f;
            Assert.That(ClipEnd.Wrap(1.2f + length, length), Is.EqualTo(ClipEnd.Wrap(1.2f, length)).Within(1e-4f));
            Assert.That(ClipEnd.Wrap(1.2f + 3 * length, length), Is.EqualTo(ClipEnd.Wrap(1.2f, length)).Within(1e-4f));
        }

        [Test]
        public void WrapAtExactLengthReturnsNearTheStart()
        {
            const float length = 4.017f;
            Assert.That(ClipEnd.Wrap(length, length), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(ClipEnd.Wrap(3 * length, length), Is.EqualTo(0f).Within(1e-4f));
        }

        [Test]
        public void WrapNeverFallsOnOrPastTheLimit()
        {
            const float length = 4.017f;
            for (var t = 0f; t < 10f * length; t += 0.037f)
            {
                var wrapped = ClipEnd.Wrap(t, length);
                Assert.That(wrapped, Is.GreaterThanOrEqualTo(0f), $"t={t}");
                Assert.That(wrapped, Is.LessThanOrEqualTo(ClipEnd.Limit(length)), $"t={t}");
            }
        }

        [Test]
        public void WrapDoesNotThrowForNonPositiveOrNonFiniteLength()
        {
            Assert.That(ClipEnd.Wrap(1f, 0f), Is.EqualTo(0f));
            Assert.That(ClipEnd.Wrap(1f, -1f), Is.EqualTo(0f));
            Assert.That(ClipEnd.Wrap(1f, float.NaN), Is.EqualTo(0f));
            Assert.That(ClipEnd.Wrap(1f, float.PositiveInfinity), Is.EqualTo(0f));
        }

        [Test]
        public void WrapDoesNotThrowForNonFiniteTime()
        {
            const float length = 4.017f;
            Assert.That(ClipEnd.Wrap(float.NaN, length), Is.EqualTo(0f));
            Assert.That(ClipEnd.Wrap(float.PositiveInfinity, length), Is.EqualTo(0f));
        }
    }
}
