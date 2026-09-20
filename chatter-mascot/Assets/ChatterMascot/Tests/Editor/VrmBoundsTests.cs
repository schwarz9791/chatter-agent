using System;
using System.Collections.Generic;
using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// モデルの bounds の合成規則。
    ///
    /// ★ <b><c>Renderer</c> を作るテストは書かない。</b> mesh を持たない
    ///   <c>MeshRenderer</c> は <c>bounds</c> がゼロになるので、実質 Unity をテストする
    ///   ことになる。規則は <see cref="VrmBounds.Combine"/> 側に閉じてある。
    /// </summary>
    [TestFixture]
    public sealed class VrmBoundsTests
    {
        /// <summary>vita.vrm の実測 bounds（VrmProbe の出力そのまま）。</summary>
        private static Bounds Vita() =>
            new Bounds(new Vector3(0f, 0.86f, -0.03f), new Vector3(1.39f, 1.73f, 0.55f));

        /// <summary>
        /// ★ <b>これが本命。</b> <c>new Bounds()</c> から <c>Encapsulate</c> を始めると
        ///   <b>必ず原点を含む箱</b>になり、中心が下へ引きずられて
        ///   自動フレーミングが「小さく映る」。
        /// </summary>
        [Test]
        public void SingleBoxDoesNotStretchToTheOrigin()
        {
            var one = Vita();
            var combined = VrmBounds.Combine(new[] { one });

            Assert.That(combined.center, Is.EqualTo(one.center));
            Assert.That(combined.size, Is.EqualTo(one.size));
        }

        /// <summary>
        /// ★ 空の <c>Renderer</c> に相当するサイズ 0 の箱は、<b>先頭にあっても</b>混ぜない。
        ///   <c>first</c> フラグの扱いを間違えるとここだけが落ちる。
        /// </summary>
        [Test]
        public void ZeroSizedPartsAreIgnoredEvenWhenFirst()
        {
            var body = Vita();
            var empty = new Bounds(new Vector3(100f, 100f, 100f), Vector3.zero);

            var combined = VrmBounds.Combine(new[] { empty, body, empty });

            Assert.That(combined.center, Is.EqualTo(body.center));
            Assert.That(combined.size, Is.EqualTo(body.size));
        }

        [Test]
        public void DisjointPartsAreUnioned()
        {
            var left = new Bounds(new Vector3(-1f, 0f, 0f), Vector3.one);
            var right = new Bounds(new Vector3(1f, 0f, 0f), Vector3.one);

            var combined = VrmBounds.Combine(new[] { left, right });

            Assert.That(combined.min.x, Is.EqualTo(-1.5f).Within(1e-4f));
            Assert.That(combined.max.x, Is.EqualTo(1.5f).Within(1e-4f));
        }

        [Test]
        public void OrderDoesNotMatter()
        {
            var a = new Bounds(new Vector3(-1f, 0.5f, 0f), Vector3.one);
            var b = new Bounds(new Vector3(1f, 2f, 0.5f), new Vector3(0.5f, 1f, 2f));

            Assert.That(VrmBounds.Combine(new[] { a, b }),
                Is.EqualTo(VrmBounds.Combine(new[] { b, a })));
        }

        /// <summary>★ 何も無いときは「原点の点」を返す。呼び出し側が距離 0 で弾く。</summary>
        [Test]
        public void EmptyOrNullYieldsAnEmptyBox()
        {
            Assert.That(VrmBounds.Combine(Array.Empty<Bounds>()).size, Is.EqualTo(Vector3.zero));
            Assert.That(VrmBounds.Combine(null).size, Is.EqualTo(Vector3.zero));
            Assert.That(VrmBounds.Of(null).size, Is.EqualTo(Vector3.zero));
        }

        /// <summary>★ 破棄済みの <c>Renderer</c>（Unity の fake-null）で落ちないこと。</summary>
        [Test]
        public void NullRenderersAreSkipped()
        {
            Assert.That(VrmBounds.Of(new Renderer[] { null, null }).size, Is.EqualTo(Vector3.zero));
        }

        /// <summary>★ 何も無いときは「原点の点」を返す。<c>Of</c> / <c>Combine</c> と揃える。</summary>
        [Test]
        public void OfBonesWithNoPositionsYieldsAnEmptyBox()
        {
            Assert.That(VrmBounds.OfBones(Array.Empty<Vector3>(), 0.1f).size, Is.EqualTo(Vector3.zero));
            Assert.That(VrmBounds.OfBones(null, 0.1f).size, Is.EqualTo(Vector3.zero));
        }

        /// <summary>1点だけなら、その点を中心に margin ぶんの箱になる。</summary>
        [Test]
        public void OfBonesWithASinglePositionCentersOnIt()
        {
            var point = new Vector3(1f, 2f, 3f);
            var bounds = VrmBounds.OfBones(new[] { point }, 0.1f);

            Assert.That(bounds.center, Is.EqualTo(point));
            Assert.That(bounds.size, Is.EqualTo(new Vector3(0.2f, 0.2f, 0.2f)));
        }

        /// <summary>
        /// ★ <c>marginMeters</c> は各軸に<b>両側</b>効く。<c>size</c> が <c>2 * margin</c> ぶん
        ///   増えること（<c>Bounds.Expand</c> は size にしか足さないので、両側に効かせるには
        ///   呼び出し側で2倍にして渡す必要がある——そこを間違えるとここだけが落ちる）。
        /// </summary>
        [Test]
        public void MarginMetersGrowsSizeOnBothSidesOfEachAxis()
        {
            var points = new[] { new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f) };

            var noMargin = VrmBounds.OfBones(points, 0f);
            var withMargin = VrmBounds.OfBones(points, 0.5f);

            Assert.That(withMargin.size.x, Is.EqualTo(noMargin.size.x + 1f).Within(1e-4f));
            Assert.That(withMargin.size.y, Is.EqualTo(noMargin.size.y + 1f).Within(1e-4f));
            Assert.That(withMargin.size.z, Is.EqualTo(noMargin.size.z + 1f).Within(1e-4f));
        }

        /// <summary>
        /// ★ <b>これが <c>OfBones</c> の存在理由そのもの。</b> <c>Renderer.bounds</c> は
        ///   姿勢を反映しないので T ポーズのままだが、ボーン位置から組めば
        ///   腕を下ろした点群のほうが実際に <c>size.x</c> が小さくなる。
        /// </summary>
        [Test]
        public void ArmsDownBonesAreNarrowerThanTPoseBones()
        {
            var tPose = new[]
            {
                new Vector3(-0.7f, 1.3f, 0f),   // 左手（横に伸ばした腕）
                new Vector3(0.7f, 1.3f, 0f),    // 右手
                new Vector3(0f, 1.7f, 0f),      // 頭
                new Vector3(0f, 0.9f, 0f),      // 腰
            };
            var armsDown = new[]
            {
                new Vector3(-0.2f, 0.6f, 0f),   // 左手（下ろした腕）
                new Vector3(0.2f, 0.6f, 0f),    // 右手
                new Vector3(0f, 1.7f, 0f),      // 頭
                new Vector3(0f, 0.9f, 0f),      // 腰
            };

            var tPoseBounds = VrmBounds.OfBones(tPose, 0.1f);
            var armsDownBounds = VrmBounds.OfBones(armsDown, 0.1f);

            Assert.That(armsDownBounds.size.x, Is.LessThan(tPoseBounds.size.x));
        }

        /// <summary>★ 腕（上腕・前腕・手）は箱に入れない。起動直後の T ポーズが幅を決めてしまう原因。</summary>
        [Test]
        public void IsFramingBoneIsFalseForArmsAndFingers()
        {
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.LeftUpperArm), Is.False);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.RightUpperArm), Is.False);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.LeftLowerArm), Is.False);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.RightLowerArm), Is.False);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.LeftHand), Is.False);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.RightHand), Is.False);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.LeftThumbProximal), Is.False);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.RightLittleDistal), Is.False);
        }

        /// <summary>
        /// ★ <c>LastBone</c> は実ボーンではなく <c>HumanBodyBones</c> 列挙の終端を示す番兵だが、
        ///   <c>Enum.GetValues</c> の結果には含まれてしまう。<c>MeasureBounds</c> の
        ///   <c>foreach</c> がここで確実に弾くことを固定する。
        /// </summary>
        [Test]
        public void IsFramingBoneIsFalseForTheLastBoneSentinel()
        {
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.LastBone), Is.False);
        }

        /// <summary>★ 肩は残す —— 胴の幅を決めるのはこちら。体幹・頭・脚・足も残す。</summary>
        [Test]
        public void IsFramingBoneIsTrueForShouldersTorsoHeadAndLegs()
        {
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.LeftShoulder), Is.True);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.RightShoulder), Is.True);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.Hips), Is.True);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.Spine), Is.True);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.Head), Is.True);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.LeftUpperLeg), Is.True);
            Assert.That(VrmBounds.IsFramingBone(HumanBodyBones.RightFoot), Is.True);
        }

        /// <summary>
        /// ボーン付きの点群から、<see cref="VrmBounds.IsFramingBone"/> が <c>true</c> を返すものだけを
        /// 取り出す。<c>VrmStage.MeasureBounds</c> の <c>foreach</c> と同じ絞り込みをテスト側でも行う。
        /// </summary>
        private static Vector3[] FramingPositionsOnly(IReadOnlyDictionary<HumanBodyBones, Vector3> bones)
        {
            var positions = new List<Vector3>();
            foreach (var pair in bones)
            {
                if (VrmBounds.IsFramingBone(pair.Key)) positions.Add(pair.Value);
            }
            return positions.ToArray();
        }

        /// <summary>T ポーズ（腕を横に伸ばした状態）。肩・頭・腰の位置は <see cref="ArmsDownWithShoulders"/> と揃える。</summary>
        private static Dictionary<HumanBodyBones, Vector3> TPoseWithShoulders() => new Dictionary<HumanBodyBones, Vector3>
        {
            { HumanBodyBones.Hips, new Vector3(0f, 0.9f, 0f) },
            { HumanBodyBones.Head, new Vector3(0f, 1.7f, 0f) },
            { HumanBodyBones.LeftShoulder, new Vector3(-0.2f, 1.5f, 0f) },
            { HumanBodyBones.RightShoulder, new Vector3(0.2f, 1.5f, 0f) },
            { HumanBodyBones.LeftUpperArm, new Vector3(-0.5f, 1.5f, 0f) },
            { HumanBodyBones.RightUpperArm, new Vector3(0.5f, 1.5f, 0f) },
            { HumanBodyBones.LeftHand, new Vector3(-0.9f, 1.5f, 0f) },
            { HumanBodyBones.RightHand, new Vector3(0.9f, 1.5f, 0f) },
        };

        /// <summary>腕を下ろした状態。肩・頭・腰は <see cref="TPoseWithShoulders"/> と同じ位置。</summary>
        private static Dictionary<HumanBodyBones, Vector3> ArmsDownWithShoulders() => new Dictionary<HumanBodyBones, Vector3>
        {
            { HumanBodyBones.Hips, new Vector3(0f, 0.9f, 0f) },
            { HumanBodyBones.Head, new Vector3(0f, 1.7f, 0f) },
            { HumanBodyBones.LeftShoulder, new Vector3(-0.2f, 1.5f, 0f) },
            { HumanBodyBones.RightShoulder, new Vector3(0.2f, 1.5f, 0f) },
            { HumanBodyBones.LeftUpperArm, new Vector3(-0.25f, 1.2f, 0f) },
            { HumanBodyBones.RightUpperArm, new Vector3(0.25f, 1.2f, 0f) },
            { HumanBodyBones.LeftHand, new Vector3(-0.3f, 0.6f, 0f) },
            { HumanBodyBones.RightHand, new Vector3(0.3f, 0.6f, 0f) },
        };

        /// <summary>
        /// ★ <b>これがこの修正の目的そのもの。</b> 腕を <see cref="VrmBounds.IsFramingBone"/> で
        ///   除いてから幅を測ると、T ポーズ（腕を横に伸ばした状態）と腕を下ろした状態とで
        ///   ほぼ同じ幅になる —— 肩幅だけで決まるようになるので、起動直後（T ポーズ）と
        ///   VRMA 適用後（腕が下りた状態）でキャラの大きさが変わらず、ポップが消える。
        /// </summary>
        [Test]
        public void ExcludingArmsMakesTPoseAndArmsDownWidthsNearlyEqual()
        {
            var tPoseBounds = VrmBounds.OfBones(FramingPositionsOnly(TPoseWithShoulders()), 0.1f);
            var armsDownBounds = VrmBounds.OfBones(FramingPositionsOnly(ArmsDownWithShoulders()), 0.1f);

            Assert.That(armsDownBounds.size.x, Is.EqualTo(tPoseBounds.size.x).Within(1e-4f));
        }
    }
}
