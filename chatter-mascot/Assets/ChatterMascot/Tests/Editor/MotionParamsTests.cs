using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="MotionParams.Default"/>。感情モーションの間引き・クールダウン・
    /// 待機の小ネタ間隔の出荷値を固定する。
    /// </summary>
    [TestFixture]
    public sealed class MotionParamsTests
    {
        [Test]
        public void DefaultPinsShippedValues()
        {
            var p = MotionParams.Default;

            Assert.That(p.FadeSeconds, Is.EqualTo(0.5f));
            Assert.That(p.CooldownSeconds, Is.EqualTo(1.0));
            Assert.That(p.SameCategoryCooldownSeconds, Is.EqualTo(15.0));
            Assert.That(p.AccentMinSeconds, Is.EqualTo(30.0));
            Assert.That(p.AccentMaxSeconds, Is.EqualTo(60.0));
        }
    }
}
