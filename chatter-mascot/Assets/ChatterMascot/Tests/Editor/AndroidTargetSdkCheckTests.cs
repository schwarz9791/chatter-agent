using ChatterMascot.EditorTools;
using NUnit.Framework;
using UnityEditor.Build;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class AndroidTargetSdkCheckTests
    {
        [TestCase((int)AndroidPlayerSettings.TargetSdkVersion)]
        public void AcceptsThePinnedValue(int targetSdk)
        {
            Assert.DoesNotThrow(() => AndroidTargetSdkCheck.Check(targetSdk));
        }

        [TestCase(0)]
        [TestCase(37)]
        public void RejectsAutomaticAndLocalNetworkPermissionLevels(int targetSdk)
        {
            Assert.Throws<BuildFailedException>(() => AndroidTargetSdkCheck.Check(targetSdk));
        }
    }
}
