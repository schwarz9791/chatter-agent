using ChatterMascot.Net;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class ReleaseCheckTests
    {
        private static string Release(string tag, bool draft = false, bool prerelease = false) =>
            $"{{\"tag_name\":\"{tag}\",\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(prerelease ? "true" : "false")}}}";

        private static string List(params string[] items) => "[" + string.Join(",", items) + "]";

        [Test]
        public void ReturnsTheLargestNewerVersion()
        {
            var json = List(Release("mascot-v0.2.0"), Release("mascot-v0.3.1"), Release("mascot-v0.1.0"));
            Assert.That(ReleaseCheck.Newer(json, "0.2.0"), Is.EqualTo("0.3.1"));
        }

        [Test]
        public void NullWhenSame() =>
            Assert.That(ReleaseCheck.Newer(List(Release("mascot-v0.2.0")), "0.2.0"), Is.Null);

        [Test]
        public void NullWhenOnlyOlder() =>
            Assert.That(ReleaseCheck.Newer(List(Release("mascot-v0.1.0")), "0.2.0"), Is.Null);

        [Test]
        public void IgnoresDraftAndPrerelease()
        {
            var json = List(Release("mascot-v0.9.0", draft: true), Release("mascot-v0.8.0", prerelease: true));
            Assert.That(ReleaseCheck.Newer(json, "0.2.0"), Is.Null);
        }

        [Test]
        public void IgnoresOtherComponents() =>
            Assert.That(ReleaseCheck.Newer(List(Release("agent-v9.0.0")), "0.2.0"), Is.Null);

        [Test]
        public void SkipsMalformedTags()
        {
            var json = List(Release("mascot-v1.0"), Release("mascot-v0.3.0-rc1"), Release("mascot-v0.3.0"));
            Assert.That(ReleaseCheck.Newer(json, "0.2.0"), Is.EqualTo("0.3.0"));
        }

        [Test]
        public void NullWhenCurrentIsDevBuild() =>
            Assert.That(ReleaseCheck.Newer(List(Release("mascot-v0.3.0")), "0.0.0-dev"), Is.Null);

        [TestCase("not json")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("{}")]
        [TestCase("[]")]
        [TestCase("[1,null,{\"tag_name\":5}]")]
        public void NullOnBrokenOrEmpty(string json) =>
            Assert.That(ReleaseCheck.Newer(json, "0.2.0"), Is.Null);

        [TestCase(RuntimePlatform.Android, null, true)]
        [TestCase(RuntimePlatform.Android, "", true)]
        [TestCase(RuntimePlatform.Android, "com.android.vending", false)]
        [TestCase(RuntimePlatform.OSXPlayer, null, false)]
        [TestCase(RuntimePlatform.OSXEditor, "", false)]
        public void ShouldCheck(RuntimePlatform platform, string installer, bool expected) =>
            Assert.That(ReleaseCheck.ShouldCheck(platform, installer), Is.EqualTo(expected));
    }
}
