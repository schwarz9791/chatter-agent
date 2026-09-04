using System;
using System.Linq;
using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// VRMA に無い指ボーンへ既定で当てる丸め（<see cref="FingerPose"/>）。#88。
    ///
    /// ★ <c>IsFinger</c> / <c>FingerBones</c> / <c>RelaxedCurl</c> の3者が矛盾しないこと
    ///   （「30本」の数え方がどこでもズレていないこと）を軸に固定する。
    /// </summary>
    [TestFixture]
    public sealed class FingerPoseTests
    {
        /// <summary><c>LastBone</c>（列挙の終端の番兵）を除いた全 <c>HumanBodyBones</c>。</summary>
        private static HumanBodyBones[] AllBones() =>
            Enum.GetValues(typeof(HumanBodyBones))
                .Cast<HumanBodyBones>()
                .Where(b => b != HumanBodyBones.LastBone)
                .ToArray();

        /// <summary>
        /// <see cref="FingerPose"/> の内部実装を参照せず、公開定数だけから「このボーンなら
        /// 何度のはずか」を求める。<c>RelaxedCurl</c> の実装を裏側で検算する意味を持たせるため、
        /// あえて実装のテーブル（segments 配列）を再利用しない。
        /// </summary>
        private static float ExpectedMagnitudeDegrees(HumanBodyBones bone)
        {
            var name = bone.ToString();
            var isThumb = name.Contains("Thumb");

            if (name.EndsWith("Proximal", StringComparison.Ordinal))
            {
                return isThumb ? FingerPose.ThumbProximalDegrees : FingerPose.ProximalDegrees;
            }
            if (name.EndsWith("Intermediate", StringComparison.Ordinal))
            {
                return isThumb ? FingerPose.ThumbIntermediateDegrees : FingerPose.IntermediateDegrees;
            }
            if (name.EndsWith("Distal", StringComparison.Ordinal))
            {
                return isThumb ? FingerPose.ThumbDistalDegrees : FingerPose.DistalDegrees;
            }

            throw new ArgumentException($"指ボーンではない: {bone}");
        }

        [Test]
        public void IsFingerIsTrueForExactlyThirtyBones()
        {
            var fingerCount = AllBones().Count(FingerPose.IsFinger);
            Assert.That(fingerCount, Is.EqualTo(30));
        }

        [Test]
        public void FingerBonesHasThirtyDistinctEntriesAllSatisfyingIsFinger()
        {
            var bones = FingerPose.FingerBones.ToArray();

            Assert.That(bones.Length, Is.EqualTo(30));
            Assert.That(bones.Distinct().Count(), Is.EqualTo(30));
            Assert.That(bones.All(FingerPose.IsFinger), Is.True);
        }

        [Test]
        public void NonFingerBonesYieldIdentity()
        {
            foreach (var bone in AllBones().Where(b => !FingerPose.IsFinger(b)))
            {
                Assert.That(FingerPose.RelaxedCurl(bone), Is.EqualTo(Quaternion.identity),
                    $"{bone} は指ではないので identity のはず");
            }
        }

        /// <summary>
        /// 人差し指〜小指は非 identity（曲げてある）。親指は <c>ThumbXDegrees</c> が示す角度
        /// （いまは 0＝定数を読む形で identity と等価）——ハードコードで「identity」と決め打ちせず、
        /// 定数を経由することで #88 の後続で親指が有効化されてもこのテストは追随する。
        /// </summary>
        [Test]
        public void EveryFingerBoneMatchesItsExpectedMagnitude()
        {
            foreach (var bone in FingerPose.FingerBones)
            {
                var expected = ExpectedMagnitudeDegrees(bone);
                var actual = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone));
                Assert.That(actual, Is.EqualTo(expected).Within(1e-3f), $"{bone}");
            }
        }

        [Test]
        public void IndexToLittleBonesAreNeverIdentity()
        {
            var indexToLittle = FingerPose.FingerBones.Where(b => !b.ToString().Contains("Thumb"));

            foreach (var bone in indexToLittle)
            {
                var angle = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone));
                Assert.That(angle, Is.GreaterThan(0f), $"{bone}");
            }
        }

        [Test]
        public void ThumbBonesEqualIdentityWhileThumbConstantsAreZero()
        {
            Assert.That(FingerPose.ThumbProximalDegrees, Is.EqualTo(0f));
            Assert.That(FingerPose.ThumbIntermediateDegrees, Is.EqualTo(0f));
            Assert.That(FingerPose.ThumbDistalDegrees, Is.EqualTo(0f));

            var thumbBones = FingerPose.FingerBones.Where(b => b.ToString().Contains("Thumb"));
            foreach (var bone in thumbBones)
            {
                var angle = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone));
                Assert.That(angle, Is.EqualTo(ExpectedMagnitudeDegrees(bone)).Within(1e-3f), $"{bone}");
            }
        }

        /// <summary>
        /// 左右対称。<see cref="FingerPose.RelaxedCurl"/> のコメントどおり「右手が負・左手が正」
        /// なので、<c>Quaternion.Angle(left, right)</c> は区間の丸め角の <b>2倍</b>になり、
        /// 生の <c>z</c> 成分（Z 軸まわり回転の符号がそのまま乗る）は左右で符号が反転する
        /// （0度の親指は 0 == -0 として自然に成立する）。
        /// </summary>
        [Test]
        public void LeftAndRightMirrorAroundTheSameMagnitude()
        {
            var segments = new (HumanBodyBones Left, HumanBodyBones Right)[]
            {
                (HumanBodyBones.LeftThumbProximal, HumanBodyBones.RightThumbProximal),
                (HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.RightThumbIntermediate),
                (HumanBodyBones.LeftThumbDistal, HumanBodyBones.RightThumbDistal),
                (HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal),
                (HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.RightIndexIntermediate),
                (HumanBodyBones.LeftIndexDistal, HumanBodyBones.RightIndexDistal),
                (HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal),
                (HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate),
                (HumanBodyBones.LeftMiddleDistal, HumanBodyBones.RightMiddleDistal),
                (HumanBodyBones.LeftRingProximal, HumanBodyBones.RightRingProximal),
                (HumanBodyBones.LeftRingIntermediate, HumanBodyBones.RightRingIntermediate),
                (HumanBodyBones.LeftRingDistal, HumanBodyBones.RightRingDistal),
                (HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal),
                (HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.RightLittleIntermediate),
                (HumanBodyBones.LeftLittleDistal, HumanBodyBones.RightLittleDistal),
            };

            foreach (var (left, right) in segments)
            {
                var l = FingerPose.RelaxedCurl(left);
                var r = FingerPose.RelaxedCurl(right);
                var expectedMagnitude = ExpectedMagnitudeDegrees(left);

                Assert.That(Quaternion.Angle(l, r), Is.EqualTo(expectedMagnitude * 2f).Within(1e-3f),
                    $"{left} vs {right}");
                Assert.That(l.z, Is.EqualTo(-r.z).Within(1e-6f), $"{left}.z vs {right}.z");
            }
        }

        [Test]
        public void AllAnglesStayWithin45Degrees()
        {
            foreach (var bone in FingerPose.FingerBones)
            {
                var angle = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone));
                Assert.That(angle, Is.LessThanOrEqualTo(45f), $"{bone}");
            }
        }
    }
}
