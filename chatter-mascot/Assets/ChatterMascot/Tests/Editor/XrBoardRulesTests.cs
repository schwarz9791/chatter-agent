using ChatterMascot.Xr;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class XrBoardRulesTests
    {
        private const float Tolerance = 1e-4f;

        [Test]
        public void SitsAtEyeHeight()
        {
            var (position, _) = XrBoardRules.InitialPose(new Vector3(0f, 1.6f, 0f), new Vector3(0f, 0f, 1f), Vector3.forward);
            Assert.That(position.y, Is.EqualTo(1.6f).Within(Tolerance));
        }

        [Test]
        public void SitsBehindTheCharacterOnTheViewerLine()
        {
            var (position, _) = XrBoardRules.InitialPose(new Vector3(1f, 1.6f, 1f), new Vector3(1f, 0f, 3f), Vector3.right);
            Assert.That(position.x, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(position.z, Is.EqualTo(3f + XrBoardRules.BehindCharacter).Within(Tolerance));
        }

        [Test]
        public void FacesTheViewerHorizontally()
        {
            var (position, rotation) = XrBoardRules.InitialPose(new Vector3(0f, 1.6f, 0f), new Vector3(2f, 0f, 0f), Vector3.forward);
            var expected = (position - new Vector3(0f, position.y, 0f)).normalized;
            Assert.That(Vector3.Dot(rotation * Vector3.forward, expected), Is.EqualTo(1f).Within(Tolerance));
            Assert.That((rotation * Vector3.up).y, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void FallsBackWhenTheCharacterIsRightAtTheViewer()
        {
            var (position, rotation) = XrBoardRules.InitialPose(new Vector3(0f, 1.6f, 0f), new Vector3(0f, 0f, 0f), new Vector3(1f, 0.5f, 0f));
            Assert.That(position.x, Is.EqualTo(XrBoardRules.BehindCharacter).Within(Tolerance));
            Assert.That(position.z, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(Vector3.Dot(rotation * Vector3.forward, Vector3.right), Is.EqualTo(1f).Within(Tolerance));
        }
    }
}
