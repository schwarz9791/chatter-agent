using System;
using System.Collections.Generic;
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
    /// ★ #88 の後続で、<c>RelaxedCurl(bone, now)</c>（時間ベースの揺れ）が加わった。
    ///   静的な基準（1引数版）を固定する上のテスト群と、揺れ（2引数版）を固定する
    ///   下のテスト群に分けてある——揺れは基準を壊さない前提（振幅の範囲内で振動するだけ）
    ///   なので、両方が別々に緑である必要がある。
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
        /// 左右対称。<see cref="FingerPose.RelaxedCurl(HumanBodyBones)"/> のコメントどおり「右手が負・左手が正」
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

        // ★★ #88 の後続（時間ベースの指の揺れ）。ここから下は <see cref="FingerPose.RelaxedCurl(HumanBodyBones, double)"/>
        //   （揺れあり）を軸に固定する。上のテスト群は静的な基準（1引数版）を固定していて、
        //   揺れは「その基準を中心に振動する」だけなので壊してよい前提を増やさない。

        /// <summary>指以外を含めずに済むよう、親指を除いた30本のうち20本を取り出す共通ヘルパー。</summary>
        private static IEnumerable<HumanBodyBones> NonThumbFingerBones() =>
            FingerPose.FingerBones.Where(b => !b.ToString().Contains("Thumb"));

        [Test]
        public void SwayStaysWithinAmplitudeAroundTheBase()
        {
            // ★ SwaySeconds（4秒）の中に収まる、周期の端点を含まないサンプル
            var nowSamples = new[] { 0.0, 0.5, 1.0, 1.7, 2.3, 3.1, 3.9 };

            foreach (var bone in NonThumbFingerBones())
            {
                var baseAngle = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone));

                foreach (var now in nowSamples)
                {
                    var angle = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone, now));
                    Assert.That(Mathf.Abs(angle - baseAngle), Is.LessThanOrEqualTo(FingerPose.SwayDegrees + 1e-3f),
                        $"{bone} at now={now}");
                }
            }
        }

        [Test]
        public void SwayIsPeriodic()
        {
            const double t = 1.3; // SwaySeconds（4秒）の倍数ではない、任意の位相

            foreach (var bone in NonThumbFingerBones())
            {
                var a = FingerPose.RelaxedCurl(bone, t);
                var b = FingerPose.RelaxedCurl(bone, t + FingerPose.SwaySeconds);
                Assert.That(Quaternion.Angle(a, b), Is.LessThan(1e-3f), $"{bone}");
            }
        }

        [Test]
        public void SwayIsContinuousOverASmallTimeStep()
        {
            const double t = 2.0;
            const double dt = 1.0 / 60.0; // 30fps でも十分に細かい、1フレームぶんの時間

            foreach (var bone in NonThumbFingerBones())
            {
                var a = FingerPose.RelaxedCurl(bone, t);
                var b = FingerPose.RelaxedCurl(bone, t + dt);
                Assert.That(Quaternion.Angle(a, b), Is.LessThan(1f), $"{bone}");
            }
        }

        [Test]
        public void ThumbDoesNotSwayWhileItsBaseIsZero()
        {
            var thumbBones = FingerPose.FingerBones.Where(b => b.ToString().Contains("Thumb"));
            var nowSamples = new[] { 0.0, 1.0, 2.0, 3.0, 100.0 };

            foreach (var bone in thumbBones)
            {
                foreach (var now in nowSamples)
                {
                    Assert.That(FingerPose.RelaxedCurl(bone, now), Is.EqualTo(Quaternion.identity),
                        $"{bone} at now={now}");
                }
            }
        }

        /// <summary>
        /// 左右は同位相・同符号の約束でミラーする（揺れを逆位相にしないこと）。
        /// <see cref="LeftAndRightMirrorAroundTheSameMagnitude"/> の揺れあり版。
        /// </summary>
        [Test]
        public void LeftAndRightSwayInMirror()
        {
            var segments = new (HumanBodyBones Left, HumanBodyBones Right)[]
            {
                (HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal),
                (HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate),
                (HumanBodyBones.LeftRingDistal, HumanBodyBones.RightRingDistal),
                (HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal),
            };
            var nowSamples = new[] { 0.0, 0.7, 1.9, 3.3 };

            foreach (var (left, right) in segments)
            {
                foreach (var now in nowSamples)
                {
                    var l = FingerPose.RelaxedCurl(left, now);
                    var r = FingerPose.RelaxedCurl(right, now);
                    var angleL = Quaternion.Angle(Quaternion.identity, l);

                    Assert.That(l.z, Is.EqualTo(-r.z).Within(1e-6f), $"{left}.z vs {right}.z at now={now}");
                    Assert.That(Quaternion.Angle(l, r), Is.EqualTo(angleL * 2f).Within(1e-3f),
                        $"{left} vs {right} at now={now}");
                }
            }
        }

        /// <summary>
        /// 4本が「一枚板」のように同じタイミングで動かないこと（<see cref="FingerPose.SwayLagPerFingerRadians"/>）。
        ///
        /// ★ <c>now = 0</c> は <c>Oscillator.Phase(0, SwaySeconds) == 0</c> になる、位相を暗算できる点
        ///   （<c>Runtime/Vrm/Oscillator.cs</c> の <c>Phase</c> 参照）。人差し指（<c>fingerIndex</c> 0）の
        ///   位相ずれは 0 なので <c>sin(0) == 0</c> ＝ズレ無し、小指（<c>fingerIndex</c> 3）は
        ///   <c>SwayLagPerFingerRadians</c> × 3 = 1.5 ラジアンだけずれるので <c>sin(1.5) ≠ 0</c>。
        /// </summary>
        [Test]
        public void FingersDoNotMoveAsOneBlock()
        {
            const double now = 0.0;

            var indexAngle = Quaternion.Angle(Quaternion.identity,
                FingerPose.RelaxedCurl(HumanBodyBones.RightIndexProximal, now));
            var littleAngle = Quaternion.Angle(Quaternion.identity,
                FingerPose.RelaxedCurl(HumanBodyBones.RightLittleProximal, now));

            var indexBase = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(HumanBodyBones.RightIndexProximal));
            var littleBase = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(HumanBodyBones.RightLittleProximal));

            var indexOffset = indexAngle - indexBase;
            var littleOffset = littleAngle - littleBase;
            var expectedLittleOffset = FingerPose.SwayDegrees * Mathf.Sin(3f * FingerPose.SwayLagPerFingerRadians);

            Assert.That(indexOffset, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(littleOffset, Is.EqualTo(expectedLittleOffset).Within(1e-3f));
            Assert.That(Mathf.Abs(indexOffset - littleOffset), Is.GreaterThan(1e-3f),
                "人差し指と小指が同じタイミングで動いている（一枚板になっている）");
        }

        /// <summary>
        /// sin の平均は1周期でゼロなので、揺れは基準角を平均として振動する（片側に偏らない）。
        ///
        /// ★ 位相ずれ（<see cref="FingerPose.SwayLagPerFingerRadians"/>）が付いていても、
        ///   等間隔サンプルの和が1周期ぶんでゼロになる性質（離散フーリエの直交性）は
        ///   位相にはよらないので、どの指で確かめても同じ結果になる。
        /// </summary>
        [Test]
        public void SwayAveragesToTheBase()
        {
            const int sampleCount = 16;
            const HumanBodyBones bone = HumanBodyBones.RightMiddleIntermediate;
            var baseAngle = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone));

            var sum = 0f;
            for (var i = 0; i < sampleCount; i++)
            {
                var now = FingerPose.SwaySeconds * i / sampleCount;
                sum += Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone, now));
            }
            var mean = sum / sampleCount;

            Assert.That(mean, Is.EqualTo(baseAngle).Within(0.05f));
        }

        /// <summary>
        /// <see cref="Oscillator.Phase"/> の「float へ落とす前に周期で畳む」保証そのものを、
        /// 指の揺れを通して確かめる。畳んでいなければ float の刻み幅が位相差を上回り、
        /// 揺れが範囲外に飛んだり止まって見えたりする（Oscillator.cs の doc 参照）。
        /// </summary>
        [Test]
        public void LargeNowDoesNotBreakTheSway()
        {
            const double now = 7.0 * 86400.0; // 1週間、点けっぱなしを想定した値

            foreach (var bone in NonThumbFingerBones())
            {
                var baseAngle = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone));
                var angle = Quaternion.Angle(Quaternion.identity, FingerPose.RelaxedCurl(bone, now));

                Assert.That(Mathf.Abs(angle - baseAngle), Is.LessThanOrEqualTo(FingerPose.SwayDegrees + 1e-3f),
                    $"{bone}");
            }
        }
    }
}
