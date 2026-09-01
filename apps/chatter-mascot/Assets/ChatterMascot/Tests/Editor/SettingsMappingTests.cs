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
        /// ★★ <c>headroom</c> は<b>大きいほどキャラが小さい</b>。UI の「大きさ」とは向きが逆で、
        ///   素直に代入すると<b>スライダーを右に振るほど小さくなる</b>。
        /// </summary>
        [Test]
        public void BiggerScaleMeansSmallerHeadroom()
        {
            Assert.That(
                SettingsMapping.HeadroomFor(2f),
                Is.LessThan(SettingsMapping.HeadroomFor(0.5f)));
        }

        [Test]
        public void ScaleOneKeepsTheShippedHeadroom()
        {
            Assert.That(SettingsMapping.HeadroomFor(1f), Is.EqualTo(SettingsMapping.DefaultHeadroom));
        }

        [Test]
        public void HeadroomAndScaleRoundTrip()
        {
            foreach (var scale in new[] { 0.5f, 0.8f, 1f, 1.4f, 2f })
            {
                Assert.That(
                    SettingsMapping.ScaleFor(SettingsMapping.HeadroomFor(scale)),
                    Is.EqualTo(scale).Within(0.0001f),
                    $"scale={scale} が往復しない");
            }
        }

        /// <summary>★ 0 で割らないこと（カメラがモデルの中に入る）</summary>
        [Test]
        public void ScaleIsClampedBeforeDividing()
        {
            Assert.That(SettingsMapping.HeadroomFor(0f), Is.GreaterThan(0f));
            Assert.That(SettingsMapping.HeadroomFor(-5f), Is.GreaterThan(0f));
            Assert.That(SettingsMapping.ScaleFor(0f), Is.EqualTo(1f));
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
        /// ★★ <c>&lt; 1</c> で判定すると<b>大きくする側が黙って効かなくなる</b>
        ///   （範囲が 0.0〜2.0 なので）。
        /// </summary>
        [Test]
        public void NeedsTheVolumeArgumentOnBothSidesOfUnity()
        {
            Assert.That(SettingsMapping.NeedsVolumeArgument(0.3f), Is.True, "小さくする側");
            Assert.That(SettingsMapping.NeedsVolumeArgument(1.5f), Is.True, "★ 大きくする側");
            Assert.That(SettingsMapping.NeedsVolumeArgument(1f), Is.False, "等倍では引数を増やさない");
        }

        /// <summary>★ float の裸の等値比較を書かない（丸めた後でも 1.0f ちょうどとは限らない）</summary>
        [Test]
        public void TreatsNearlyUnityAsUnity()
        {
            Assert.That(SettingsMapping.NeedsVolumeArgument(1.0000001f), Is.False);
            Assert.That(SettingsMapping.NeedsVolumeArgument(0.9999999f), Is.False);
        }
    }
}
