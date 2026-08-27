using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 自動フレーミング。
    ///
    /// 数値は同梱モデル <c>vita.vrm</c> の実測
    /// （<c>./scripts/run.sh ChatterMascot.EditorTools.VrmProbe.Report</c> が出す
    /// <c>Renderer.bounds</c> の合成）。VRM 1.0 は<b>レストポーズが T ポーズ必須</b>なので、
    /// 横幅は<b>広げた腕</b>で決まる。
    /// </summary>
    [TestFixture]
    public sealed class VrmFramingTests
    {
        private const float Fov = 60f;

        /// <summary>vita.vrm の実測 bounds（VrmProbe の出力そのまま）。</summary>
        private static Bounds Vita() =>
            new Bounds(new Vector3(0f, 0.86f, -0.03f), new Vector3(1.39f, 1.73f, 0.55f));

        /// <summary>★ 縦長のウィンドウでは水平側が支配する。#59 で腕が下りると反転する。</summary>
        [Test]
        public void PortraitWindowIsDominatedByTheTPose()
        {
            VrmFraming.Solve(Vita(), Fov, 250f / 400f, 1f, out var axis);
            Assert.That(axis, Is.EqualTo(FramingAxis.Horizontal));
        }

        /// <summary>★ #59 で <c>VrmBounds.OfBones</c> が入り、これが実際に起きるようになった。</summary>
        [Test]
        public void ArmsDownMakesItVertical()
        {
            // 腕を下ろしたぶんだけ幅を詰めた仮のポーズ
            var slim = new Bounds(Vita().center, new Vector3(0.6f, 1.73f, 0.55f));
            VrmFraming.Solve(slim, Fov, 250f / 400f, 1f, out var axis);
            Assert.That(axis, Is.EqualTo(FramingAxis.Vertical));
        }

        [Test]
        public void NarrowerWindowNeedsMoreDistance()
        {
            var wide = VrmFraming.Solve(Vita(), Fov, 600f / 800f, 1f, out _);
            var narrow = VrmFraming.Solve(Vita(), Fov, 250f / 400f, 1f, out _);
            Assert.That(narrow, Is.GreaterThan(wide));
        }

        /// <summary>★ 垂直 FOV から出した距離で、縦もはみ出していないこと。</summary>
        [Test]
        public void ModelFitsBothAxes()
        {
            var bounds = Vita();
            var aspect = 250f / 400f;
            var distance = VrmFraming.Solve(bounds, Fov, aspect, 1f, out _) - bounds.extents.z;

            var tanHalfVertical = Mathf.Tan(Fov * 0.5f * Mathf.Deg2Rad);
            Assert.That(distance * tanHalfVertical, Is.GreaterThanOrEqualTo(bounds.extents.y - 1e-4f));
            Assert.That(distance * tanHalfVertical * aspect, Is.GreaterThanOrEqualTo(bounds.extents.x - 1e-4f));
        }

        [Test]
        public void HeadroomPushesTheCameraBack()
        {
            var tight = VrmFraming.Solve(Vita(), Fov, 0.625f, 1f, out _);
            var loose = VrmFraming.Solve(Vita(), Fov, 0.625f, 1.1f, out _);
            Assert.That(loose, Is.GreaterThan(tight));
        }

        /// <summary>★ 退化した入力でカメラを飛ばさないこと（最小化 / 起動直後）。</summary>
        [Test]
        public void DegenerateInputYieldsNoDistance()
        {
            Assert.That(VrmFraming.Solve(Vita(), Fov, 0f, 1f, out _), Is.EqualTo(0f));
            Assert.That(VrmFraming.Solve(Vita(), 0f, 0.625f, 1f, out _), Is.EqualTo(0f));
            Assert.That(VrmFraming.Solve(Vita(), 180f, 0.625f, 1f, out _), Is.EqualTo(0f));
        }

        /// <summary>カメラは回転なしで +Z を見る。モデルの中心の手前に置く。</summary>
        [Test]
        public void CameraSitsInFrontOfTheModelCentre()
        {
            var bounds = Vita();
            var position = VrmFraming.CameraPosition(bounds, 2f);

            Assert.That(position.x, Is.EqualTo(bounds.center.x));
            Assert.That(position.y, Is.EqualTo(bounds.center.y));
            Assert.That(position.z, Is.EqualTo(bounds.center.z - 2f));
        }
    }
}
