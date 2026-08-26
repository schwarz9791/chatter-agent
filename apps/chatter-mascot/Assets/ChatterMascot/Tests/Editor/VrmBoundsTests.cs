using System;
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
    }
}
