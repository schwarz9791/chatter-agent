using ChatterMascot.EditorTools;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class AsmdefPlatformFilterTests
    {
        [Test]
        public void IncludePlatformsContainingTheTargetIsIncluded()
        {
            Assert.That(
                AsmdefPlatformFilter.IsIncluded("{\"includePlatforms\":[\"Android\",\"Editor\"]}", "Android"),
                Is.True);
        }

        [Test]
        public void IncludePlatformsWithoutTheTargetIsExcluded()
        {
            Assert.That(
                AsmdefPlatformFilter.IsIncluded("{\"includePlatforms\":[\"Editor\",\"macOSStandalone\"]}", "Android"),
                Is.False);
        }

        [Test]
        public void ExcludePlatformsContainingTheTargetIsExcluded()
        {
            Assert.That(
                AsmdefPlatformFilter.IsIncluded("{\"excludePlatforms\":[\"Android\"]}", "Android"),
                Is.False);
        }

        [Test]
        public void ExcludePlatformsWithoutTheTargetIsIncluded()
        {
            Assert.That(
                AsmdefPlatformFilter.IsIncluded("{\"excludePlatforms\":[\"WebGL\"]}", "Android"),
                Is.True);
        }

        [Test]
        public void BothEmptyIsIncluded()
        {
            Assert.That(
                AsmdefPlatformFilter.IsIncluded("{\"includePlatforms\":[],\"excludePlatforms\":[]}", "Android"),
                Is.True);
        }

        [Test]
        public void BothAbsentIsIncluded()
        {
            Assert.That(AsmdefPlatformFilter.IsIncluded("{}", "Android"), Is.True);
        }

        [Test]
        public void EmptyStringIsIncluded()
        {
            Assert.That(AsmdefPlatformFilter.IsIncluded("", "Android"), Is.True);
        }

        [Test]
        public void NullIsIncluded()
        {
            Assert.That(AsmdefPlatformFilter.IsIncluded(null, "Android"), Is.True);
        }

        [Test]
        public void UnparsableJsonIsIncluded()
        {
            Assert.That(AsmdefPlatformFilter.IsIncluded("{ this is not json", "Android"), Is.True);
        }

        /// <summary>
        /// ★★ 回帰。<c>Kirurobo.UniWindowController</c>（UniWindowController /
        /// UniWindowMoveHandle が属するアセンブリ）の <c>includePlatforms</c> は文字どおり
        /// これ。<see cref="AndroidSceneStripper"/> が Android でこのアセンブリのコンポーネントを
        /// 外し、macOS では外さないことを固定する。
        /// </summary>
        [Test]
        public void KiruroboAsmdefIsExcludedFromAndroidButIncludedOnMacOs()
        {
            const string json =
                "{\"includePlatforms\":[\"Editor\",\"macOSStandalone\",\"WindowsStandalone32\",\"WindowsStandalone64\"]}";

            Assert.That(AsmdefPlatformFilter.IsIncluded(json, "Android"), Is.False);
            Assert.That(AsmdefPlatformFilter.IsIncluded(json, "macOSStandalone"), Is.True);
        }
    }
}
