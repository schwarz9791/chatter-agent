using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="VrmaKeyframes"/>。VRMA の時刻キーの単調性判定と警告文の組み立てを固定する（#103）。
    /// </summary>
    [TestFixture]
    public sealed class VrmaKeyframesTests
    {
        [Test]
        public void StrictlyIncreasingTimesCountZero()
        {
            Assert.That(VrmaKeyframes.CountNonIncreasing(new float[] { 0f, 0.1f, 0.2f, 0.3f }), Is.EqualTo(0));
        }

        /// <summary>★ 末尾で同じ時刻が連続するケース（実機で踏んだ形）。</summary>
        [Test]
        public void DuplicateTrailingKeysAreCounted()
        {
            Assert.That(VrmaKeyframes.CountNonIncreasing(new float[] { 0f, 0.1f, 0.2f, 0.2f, 0.2f }), Is.EqualTo(2));
        }

        [Test]
        public void DecreasingTimeIsAlsoCounted()
        {
            Assert.That(VrmaKeyframes.CountNonIncreasing(new float[] { 0f, 0.2f, 0.1f }), Is.EqualTo(1));
        }

        [Test]
        public void FewerThanTwoKeysCountZero()
        {
            Assert.That(VrmaKeyframes.CountNonIncreasing(new float[] { 0.1f }), Is.EqualTo(0));
            Assert.That(VrmaKeyframes.CountNonIncreasing(new float[0]), Is.EqualTo(0));
        }

        [Test]
        public void NullCountsZero()
        {
            Assert.That(VrmaKeyframes.CountNonIncreasing(null), Is.EqualTo(0));
        }

        [Test]
        public void DescribeIncludesFileNameCountAndKeyCount()
        {
            var message = VrmaKeyframes.Describe("happy_01.vrma", 1, 243);

            StringAssert.Contains("happy_01.vrma", message);
            StringAssert.Contains("1 つ", message);
            StringAssert.Contains("243 キー", message);
        }
    }
}
