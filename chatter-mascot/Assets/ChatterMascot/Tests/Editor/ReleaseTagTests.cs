using ChatterMascot.EditorTools;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>release タグから決める版の規則。</summary>
    [TestFixture]
    public sealed class ReleaseTagTests
    {
        [TestCase("mascot-v0.1.0", "0.1.0", 100)]
        [TestCase("mascot-v1.2.3", "1.2.3", 10203)]
        [TestCase("mascot-v99.99.99", "99.99.99", 999999)]
        [TestCase("mascot-v0.0.1", "0.0.1", 1)]
        public void ParsesTheTag(string tag, string version, int versionCode)
        {
            Assert.That(BuildScript.TryParseReleaseTag(tag, out var actualVersion, out var actualCode), Is.True);
            Assert.That(actualVersion, Is.EqualTo(version));
            Assert.That(actualCode, Is.EqualTo(versionCode));
        }

        /// <summary>★ versionCode が 0 になる 0.0.0 は Android が受け付けない。</summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("v0.1.0")]
        [TestCase("mascot-v0.1")]
        [TestCase("mascot-v0.100.0")]
        [TestCase("mascot-v01.0.0")]
        [TestCase("mascot-v0.0.0")]
        [TestCase("mascot-v0.1.0-rc1")]
        [TestCase(" mascot-v0.1.0")]
        [TestCase("mascot-v0.1.0 ")]
        [TestCase("mascot-v0.1.0\n")]
        public void RejectsAnythingElse(string tag)
        {
            Assert.That(BuildScript.TryParseReleaseTag(tag, out _, out _), Is.False);
        }
    }
}
