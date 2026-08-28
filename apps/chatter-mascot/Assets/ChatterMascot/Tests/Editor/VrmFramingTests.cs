using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 自動フレーミング。
    ///
    /// 数値は同梱モデル <c>vita.vrm</c> の実測
    /// （<c>./scripts/run.sh ChatterMascot.EditorTools.VrmProbe.Report</c> の出力）。
    ///
    /// ★ <b>貼る値は <c>frame bounds</c> の行。<c>bounds</c> の行ではない。</b>
    ///   probe は2種類の箱を出す —— <c>bounds</c> は <c>Renderer.bounds</c> の合成
    ///   （T ポーズの静的な値。幅 1.39m）、<c>frame bounds</c> は
    ///   <c>VrmStage.MeasureBounds</c>（<b>ランタイムが実際に使う関数そのもの</b>）の出力。
    ///   #59 で切り替わったあとも <c>Renderer.bounds</c> 側を貼り続けていて、
    ///   <b>ランタイムがもう生成しない数値をこのテスト群が守っていた</b>
    ///   （PR #69 のレビューで判明）。probe の出力の形を変えたら、ここも貼り直すこと。
    /// </summary>
    [TestFixture]
    public sealed class VrmFramingTests
    {
        private const float Fov = 60f;

        /// <summary>
        /// vita.vrm の実測 bounds（<c>VrmProbe</c> の <c>frame bounds</c> の出力そのまま）。
        ///
        /// ★ <b>腕・手・指のボーンは入っていない</b>（<c>VrmBounds.IsFramingBone</c> が除く）ので、
        ///   幅は<b>肩幅</b>で決まる。VRM 1.0 のレストポーズは T ポーズ必須なので、
        ///   腕を入れると読み込み直後だけ幅 1.5m 級の箱になり、VRMA が効いた瞬間に
        ///   カメラが寄って<b>キャラが一段大きくなるのが見える</b>（#59 で直したポップ）。
        /// ★ <c>+0.1m</c> のマージン（<c>VrmStage.DefaultBoneBoundsMarginMeters</c>）込みの値。
        /// </summary>
        private static Bounds Vita() =>
            new Bounds(new Vector3(0f, 0.80f, 0.02f), new Vector3(0.35f, 1.66f, 0.31f));

        /// <summary>
        /// ★ <b>縦長のウィンドウでは垂直側が支配する。</b> W/H は 0.214 で、
        ///   ウィンドウのアスペクト（300/480 = 0.625）より細いため。
        /// ★ <b>ここが <c>Horizontal</c> に戻ったら、腕の除外
        ///   （<c>VrmBounds.IsFramingBone</c>）が壊れたと考えること。</b> #59 より前は
        ///   T ポーズの腕が箱に入っていたので水平が支配していた。
        /// </summary>
        [Test]
        public void PortraitWindowIsDominatedByHeight()
        {
            VrmFraming.Solve(Vita(), Fov, 300f / 480f, 1f, out var axis);
            Assert.That(axis, Is.EqualTo(FramingAxis.Vertical));
        }

        /// <summary>
        /// 水平が支配する分岐も残しておく。
        ///
        /// ★ 幅 1.39m は <c>VrmProbe</c> の <c>bounds</c>（<c>Renderer.bounds</c> の合成）の値で、
        ///   <b>ランタイムはもうこの箱を作らない</b>。ここでは「ウィンドウより横長の箱」を
        ///   作るための合成入力として使っているだけ —— <c>Vita()</c> の代わりに使わないこと。
        /// </summary>
        [Test]
        public void WideBoxIsDominatedByWidth()
        {
            var wideBox = new Bounds(Vita().center, new Vector3(1.39f, 1.66f, 0.31f));
            VrmFraming.Solve(wideBox, Fov, 300f / 480f, 1f, out var axis);
            Assert.That(axis, Is.EqualTo(FramingAxis.Horizontal));
        }

        /// <summary>
        /// ★ <b>ウィンドウの幅が距離に効くのは、水平が支配しているときだけ。</b>
        ///   垂直 FOV は <c>aspect</c> を含まないので、垂直支配の箱では
        ///   ウィンドウを細くしても距離は1ミリも動かない。だからここは
        ///   <c>Vita()</c> ではなく水平支配の箱で検査する。
        /// </summary>
        [Test]
        public void NarrowerWindowNeedsMoreDistance()
        {
            var wideBox = new Bounds(Vita().center, new Vector3(1.39f, 1.66f, 0.31f));
            var wide = VrmFraming.Solve(wideBox, Fov, 600f / 800f, 1f, out _);
            var narrow = VrmFraming.Solve(wideBox, Fov, 300f / 480f, 1f, out _);
            Assert.That(narrow, Is.GreaterThan(wide));
        }

        /// <summary>
        /// ★ <b>#59 の腕の除外が効いていることの、もう1つの現れ方。</b> 実測の箱は
        ///   ウィンドウより細いので、<b>ウィンドウのアスペクトを変えても距離が変わらない</b>。
        ///   ここが変わるようになったら、箱に腕が混ざって水平支配に戻っている。
        /// </summary>
        [Test]
        public void FramingBoxIgnoresTheWindowAspect()
        {
            var wide = VrmFraming.Solve(Vita(), Fov, 600f / 800f, 1f, out _);
            var narrow = VrmFraming.Solve(Vita(), Fov, 300f / 480f, 1f, out _);
            Assert.That(narrow, Is.EqualTo(wide).Within(1e-4f));
        }

        /// <summary>★ 垂直 FOV から出した距離で、縦もはみ出していないこと。</summary>
        [Test]
        public void ModelFitsBothAxes()
        {
            var bounds = Vita();
            var aspect = 300f / 480f;
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
