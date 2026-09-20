using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// モデルの向き。
    ///
    /// ★ **実機で背中が映ったので入れた。** issue #56 の「glTF→Unity の Z 反転で
    ///   モデルが −Z を向くから 180°回転は不要」は成立しなかった。
    /// </summary>
    [TestFixture]
    public sealed class VrmOrientationTests
    {
        /// <summary>Unity は左手系。+Z を向いた人物の右手は +X 側。</summary>
        [Test]
        public void ForwardIsPlusZWhenRightArmIsOnPlusX()
        {
            var forward = VrmOrientation.Forward(
                leftUpperArm: new Vector3(-0.2f, 1.4f, 0f),
                rightUpperArm: new Vector3(0.2f, 1.4f, 0f));

            Assert.That(forward.z, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(forward.x, Is.EqualTo(0f).Within(1e-4f));
        }

        /// <summary>★ これが実機の vita.vrm。カメラは +Z を見るので背中が映っていた。</summary>
        [Test]
        public void ModelFacingPlusZIsTurnedAround()
        {
            var yaw = VrmOrientation.YawToFaceCamera(
                leftUpperArm: new Vector3(-0.2f, 1.4f, 0f),
                rightUpperArm: new Vector3(0.2f, 1.4f, 0f));

            Assert.That(Mathf.Abs(yaw), Is.EqualTo(180f).Within(0.5f));
        }

        [Test]
        public void ModelAlreadyFacingTheCameraIsLeftAlone()
        {
            var yaw = VrmOrientation.YawToFaceCamera(
                leftUpperArm: new Vector3(0.2f, 1.4f, 0f),
                rightUpperArm: new Vector3(-0.2f, 1.4f, 0f));

            Assert.That(yaw, Is.EqualTo(0f));
        }

        /// <summary>真横を向いていても正面へ向け直せること。</summary>
        [Test]
        public void SidewaysModelIsTurnedByNinetyDegrees()
        {
            var yaw = VrmOrientation.YawToFaceCamera(
                leftUpperArm: new Vector3(0f, 1.4f, 0.2f),
                rightUpperArm: new Vector3(0f, 1.4f, -0.2f));

            Assert.That(Mathf.Abs(yaw), Is.EqualTo(90f).Within(0.5f));
        }

        /// <summary>★ 肩の高さのぶれで判定が揺れないこと。</summary>
        [Test]
        public void ShoulderHeightDifferenceIsIgnored()
        {
            var yaw = VrmOrientation.YawToFaceCamera(
                leftUpperArm: new Vector3(-0.2f, 1.45f, 0f),
                rightUpperArm: new Vector3(0.2f, 1.35f, 0f));

            Assert.That(Mathf.Abs(yaw), Is.EqualTo(180f).Within(0.5f));
        }

        /// <summary>★ 判定できないときは回さない（0 を返す）。</summary>
        [Test]
        public void DegenerateArmsAreLeftAlone()
        {
            var same = new Vector3(0f, 1.4f, 0f);
            Assert.That(VrmOrientation.Forward(same, same), Is.EqualTo(Vector3.zero));
            Assert.That(VrmOrientation.YawToFaceCamera(same, same), Is.EqualTo(0f));
        }
    }
}
