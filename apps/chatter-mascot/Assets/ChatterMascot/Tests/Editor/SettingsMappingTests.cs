using System.Globalization;
using System.Threading;
using ChatterMascot.Settings;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SettingsMappingTests
    {
        /// <summary>
        /// <c>WindowGeometry.DefaultWidthPoints/DefaultHeightPoints</c> の写し。
        ///
        /// ★ <b>参照できない。</b> <c>WindowGeometry</c> は <c>ChatterMascot.Desktop</c> にあり、
        ///   <c>ChatterMascot.Tests</c> の asmdef は <c>ChatterMascot.Runtime</c> しか参照していない
        ///   （Desktop は macOS/Windows 限定のプラットフォーム縛りがあり、Editor だけの
        ///   テストアセンブリに素直には足せない）。値がズレたら、ここと本番の両方を直すこと。
        /// </summary>
        private const float BaseWidth = 540f;
        private const float BaseHeight = 540f;

        /// <summary>
        /// ★★ キャラの大きさは<b>ウィンドウ</b>で変える。<c>VrmStage.headroom</c> は
        ///   「bounds をどれだけ余裕を持って収めるか」の係数で、1 を下回るとモデルが
        ///   <b>画面からはみ出す</b>（実機で頭と足が対称に欠けた）。
        /// </summary>
        [Test]
        public void BiggerScaleMeansABiggerWindow()
        {
            float smallW, smallH, bigW, bigH;
            SettingsMapping.WindowSizeFor(0.5f, BaseWidth, BaseHeight, out smallW, out smallH);
            SettingsMapping.WindowSizeFor(2f, BaseWidth, BaseHeight, out bigW, out bigH);

            Assert.That(bigW, Is.GreaterThan(smallW));
            Assert.That(bigH, Is.GreaterThan(smallH));
            Assert.That(smallW, Is.EqualTo(BaseWidth * 0.5f).Within(0.001f));
            Assert.That(bigH, Is.EqualTo(BaseHeight * 2f).Within(0.001f));
        }

        /// <summary>倍率 1.0 は出荷値そのまま</summary>
        [Test]
        public void ScaleOneKeepsTheShippedSize()
        {
            float width, height;
            SettingsMapping.WindowSizeFor(1f, BaseWidth, BaseHeight, out width, out height);

            Assert.That(width, Is.EqualTo(BaseWidth).Within(0.001f));
            Assert.That(height, Is.EqualTo(BaseHeight).Within(0.001f));
        }

        /// <summary>★ 縦横を同じ倍率で掛ける（アスペクト比を保つ）</summary>
        [Test]
        public void KeepsTheAspectRatio()
        {
            float width, height;
            SettingsMapping.WindowSizeFor(1.4f, BaseWidth, BaseHeight, out width, out height);

            Assert.That(height / width, Is.EqualTo(BaseHeight / BaseWidth).Within(0.0001f));
        }

        /// <summary>★★ 倍率は settings.json に持たない。いまの窓から読み替える</summary>
        [Test]
        public void SizeAndScaleRoundTrip()
        {
            foreach (var scale in new[] { 0.5f, 0.8f, 1f, 1.4f, 2f })
            {
                float width, height;
                SettingsMapping.WindowSizeFor(scale, BaseWidth, BaseHeight, out width, out height);

                Assert.That(
                    SettingsMapping.ScaleForWindow(height, BaseHeight),
                    Is.EqualTo(scale).Within(0.0001f),
                    $"scale={scale} が往復しない");
            }
        }

        [Test]
        public void ClampsTheScaleToTheSliderRange()
        {
            float width, height;
            SettingsMapping.WindowSizeFor(99f, BaseWidth, BaseHeight, out width, out height);
            Assert.That(height, Is.EqualTo(BaseHeight * SettingsMapping.ScaleMax).Within(0.001f));

            SettingsMapping.WindowSizeFor(-5f, BaseWidth, BaseHeight, out width, out height);
            Assert.That(height, Is.EqualTo(BaseHeight * SettingsMapping.ScaleMin).Within(0.001f));
        }

        /// <summary>★ 0 で割らないこと（窓の大きさが読めないときは等倍に倒す）</summary>
        [Test]
        public void FallsBackToUnityScaleForUnreadableSizes()
        {
            Assert.That(SettingsMapping.ScaleForWindow(0f, BaseHeight), Is.EqualTo(1f));
            Assert.That(SettingsMapping.ScaleForWindow(BaseHeight, 0f), Is.EqualTo(1f));
        }

        /// <summary>★★ スライダーから来る float は 0.7000000119 になりうる</summary>
        [Test]
        public void RoundsFloatNoiseToTheStep()
        {
            Assert.That(SettingsMapping.RoundToStep(0.7000000119f, 0.1f), Is.EqualTo(0.7f).Within(0.0001f));
            Assert.That(SettingsMapping.Format(SettingsMapping.RoundToStep(0.7000000119f, 0.1f)), Is.EqualTo("0.7"));
        }

        /// <summary>★ float のまま 2.0/0.1 を割ると 19.999998 になり、丸めが 20 の手前へ落ちる</summary>
        [Test]
        public void RoundsTheTopOfTheRangeExactly()
        {
            Assert.That(SettingsMapping.RoundToStep(2f, 0.1f), Is.EqualTo(2f).Within(0.0001f));
            Assert.That(SettingsMapping.Format(SettingsMapping.Normalize(2f, 0f, 2f, 0.1f)), Is.EqualTo("2"));
        }

        [Test]
        public void RoundToStepIsANoOpForNonPositiveSteps()
        {
            Assert.That(SettingsMapping.RoundToStep(0.73f, 0f), Is.EqualTo(0.73f));
            Assert.That(SettingsMapping.RoundToStep(0.73f, -1f), Is.EqualTo(0.73f));
        }

        [Test]
        public void NormalizeClampsAfterRounding()
        {
            Assert.That(SettingsMapping.Normalize(99f, 0f, 2f, 0.1f), Is.EqualTo(2f).Within(0.0001f));
            Assert.That(SettingsMapping.Normalize(-99f, 0f, 2f, 0.1f), Is.EqualTo(0f).Within(0.0001f));
            Assert.That(SettingsMapping.Normalize(float.NaN, 0.5f, 2f, 0.1f), Is.EqualTo(0.5f));
        }

        /// <summary>
        /// ★★ <c>InvariantCulture</c> を忘れると <c>0,5</c> になる。行き先は
        ///   <c>settings.json</c> / <c>afplay -v</c> / <c>PATCH</c> のボディで、
        ///   <b>症状が3つとも別々の場所に出る</b>。
        /// </summary>
        [Test]
        public void FormatsAndParsesWithTheInvariantCulture()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                // 小数点にカンマを使うロケール
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                Assert.That(SettingsMapping.Format(0.5f), Is.EqualTo("0.5"));
                Assert.That(SettingsMapping.Parse("0.5", 9f), Is.EqualTo(0.5f));
                // ★ ロケール依存の表記は受けない（受けると書式が2つになる）
                Assert.That(SettingsMapping.Parse("0,5", 9f), Is.EqualTo(9f));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Test]
        public void ParseFallsBackForUnreadableInput()
        {
            Assert.That(SettingsMapping.Parse(null, 1f), Is.EqualTo(1f));
            Assert.That(SettingsMapping.Parse("", 1f), Is.EqualTo(1f));
            Assert.That(SettingsMapping.Parse("はやい", 1f), Is.EqualTo(1f));
            Assert.That(SettingsMapping.Parse("NaN", 1f), Is.EqualTo(1f));
            Assert.That(SettingsMapping.Parse("Infinity", 1f), Is.EqualTo(1f));
        }

        /// <summary>
        /// ★ 小さくするときだけ引数が増える（等倍では増やさない ＝ #76 より前の挙動）。
        ///
        /// ★★ <b>上限を 1.0 に下げても <c>&lt; 1</c> の裸の比較にしないこと。</b>
        ///   以前の理由（大きくする側が効かなくなる）は消えたが、下の
        ///   <see cref="TreatsNearlyUnityAsUnity"/> が守っているものは残っている。
        /// </summary>
        [Test]
        public void NeedsTheVolumeArgumentOnlyWhenItIsNotUnity()
        {
            Assert.That(SettingsMapping.NeedsVolumeArgument(0.3f), Is.True, "小さくする側");
            Assert.That(SettingsMapping.NeedsVolumeArgument(1f), Is.False, "等倍では引数を増やさない");
        }

        /// <summary>
        /// ★★ <b>音量の上限は 1.0。</b> 1.0 超えが効くのは macOS（<c>afplay -v</c>）だけで、
        ///   Android の <c>AudioSource.volume</c> は Unity 側で 0〜1 にクランプされる。
        ///   <c>settings.json</c> は XR（#25）と共有するので、
        ///   <b>プラットフォームで意味の変わる範囲を持たせない</b>。
        /// </summary>
        [Test]
        public void CapsTheVolumeAtUnity()
        {
            Assert.That(SettingsMapping.VolumeMax, Is.EqualTo(1f));
            Assert.That(
                SettingsMapping.Normalize(
                    1.5f, SettingsMapping.VolumeMin, SettingsMapping.VolumeMax, SettingsMapping.VolumeStep),
                Is.EqualTo(1f), "★ 範囲外は握りつぶさずにクランプする");
        }

        /// <summary>★ float の裸の等値比較を書かない（丸めた後でも 1.0f ちょうどとは限らない）</summary>
        [Test]
        public void TreatsNearlyUnityAsUnity()
        {
            Assert.That(SettingsMapping.NeedsVolumeArgument(1.0000001f), Is.False);
            Assert.That(SettingsMapping.NeedsVolumeArgument(0.9999999f), Is.False);
        }

        /// <summary>
        /// ★ 30/60 の2値しか無いので、選べる値はそのまま返し、それ以外は「近い方」ではなく
        ///   既定（<see cref="SettingsMapping.DefaultFrameRate"/>）へ倒す（→ #88）。
        /// </summary>
        [Test]
        public void NormalizesTheFrameRate()
        {
            Assert.That(SettingsMapping.NormalizeFrameRate(30), Is.EqualTo(30));
            Assert.That(SettingsMapping.NormalizeFrameRate(60), Is.EqualTo(60));
            Assert.That(SettingsMapping.NormalizeFrameRate(45), Is.EqualTo(SettingsMapping.DefaultFrameRate));
            Assert.That(SettingsMapping.NormalizeFrameRate(0), Is.EqualTo(SettingsMapping.DefaultFrameRate));
            Assert.That(SettingsMapping.NormalizeFrameRate(-1), Is.EqualTo(SettingsMapping.DefaultFrameRate));
        }
    }
}
