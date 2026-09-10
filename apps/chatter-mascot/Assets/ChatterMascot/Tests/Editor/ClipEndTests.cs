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
    }
}
