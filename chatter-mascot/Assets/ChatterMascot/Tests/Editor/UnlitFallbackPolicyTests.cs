using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class UnlitFallbackPolicyTests
    {
        [Test]
        public void AppliesToAndroid()
        {
            Assert.That(UnlitFallbackPolicy.AppliesTo(RuntimePlatform.Android), Is.True);
        }

        [TestCase(RuntimePlatform.OSXPlayer)]
        [TestCase(RuntimePlatform.OSXEditor)]
        [TestCase(RuntimePlatform.WindowsPlayer)]
        [TestCase(RuntimePlatform.LinuxPlayer)]
        [TestCase(RuntimePlatform.IPhonePlayer)]
        public void DoesNotApplyToOtherPlatforms(RuntimePlatform platform)
        {
            Assert.That(UnlitFallbackPolicy.AppliesTo(platform), Is.False);
        }

        [TestCase(0, UnlitFallbackPolicy.UnlitBlend.Opaque)]
        [TestCase(1, UnlitFallbackPolicy.UnlitBlend.Cutout)]
        [TestCase(2, UnlitFallbackPolicy.UnlitBlend.Transparent)]
        public void MapsAlphaModeToUnlitBlend(int mtoonAlphaMode, UnlitFallbackPolicy.UnlitBlend expected)
        {
            Assert.That(UnlitFallbackPolicy.MapAlphaMode(mtoonAlphaMode), Is.EqualTo(expected));
        }

        /// <summary>未知の値は不透明側に倒す（描画が消える／裏抜けするより安全）。</summary>
        [TestCase(-1)]
        [TestCase(3)]
        [TestCase(999)]
        public void MapsUnknownAlphaModeToOpaque(int mtoonAlphaMode)
        {
            Assert.That(UnlitFallbackPolicy.MapAlphaMode(mtoonAlphaMode),
                        Is.EqualTo(UnlitFallbackPolicy.UnlitBlend.Opaque));
        }
    }
}
