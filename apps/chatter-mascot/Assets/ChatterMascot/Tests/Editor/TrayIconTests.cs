using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>StreamingAssets/trayTemplate.png</c>（16×16）と <c>trayTemplate@2x.png</c>（32×32）が
    /// 実在し、寸法どおりであること。
    ///
    /// ★★ <b>なぜ要るか。</b> <c>CMLoadTemplateImage</c>（<c>CMStatusItem.m</c>）は @1x と @2x を
    ///   1つの <c>NSImage</c> に入れ、<b>両 rep の最小 pixel 寸法</b>をポイントとして全 rep の
    ///   size を揃える。片方の寸法がずれると、Retina でぼやけるか非 Retina で 2 倍の大きさに
    ///   描かれる。テンプレート画像の事故は「なんとなく変」で済んでしまい気づきにくいので、
    ///   寸法をテストで固定する。<c>MenuJsonTests</c> はパスの文字列を pass-through で見るだけで、
    ///   画像そのものの実在も寸法も見ていない——今この事故を止めるテストはリポジトリに無かった。
    ///
    /// ★ <c>StreamingAssets</c> の PNG は Unity がインポートしないので
    ///   <c>AssetDatabase.LoadAssetAtPath&lt;Texture2D&gt;</c> では読めない。
    ///   <c>File.ReadAllBytes</c> して PNG の <c>IHDR</c> チャンクを直接読む
    ///   （シグネチャ8バイト + チャンク長4 + <c>IHDR</c>4 の後、幅がオフセット16..19、
    ///   高さが20..23、いずれもビッグエンディアンの4バイト符号なし整数）。
    /// </summary>
    [TestFixture]
    public sealed class TrayIconTests
    {
        // PNG シグネチャ（RFC 2083）。先頭8バイトが一致しなければ PNG ではない
        private static readonly byte[] PngSignature =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        };

        [Test]
        public void TrayTemplateIs16By16()
        {
            AssertPngSize("trayTemplate.png", 16, 16);
        }

        [Test]
        public void TrayTemplate2xIs32By32()
        {
            AssertPngSize("trayTemplate@2x.png", 32, 32);
        }

        private static void AssertPngSize(string fileName, int expectedWidth, int expectedHeight)
        {
            var path = Path.Combine(Application.dataPath, "StreamingAssets", fileName);
            Assert.That(File.Exists(path), Is.True, $"トレイ画像が見つかりません: {path}");

            var bytes = File.ReadAllBytes(path);
            Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(24), $"PNG のヘッダより短いです: {path}");

            for (var i = 0; i < PngSignature.Length; i++)
            {
                Assert.That(bytes[i], Is.EqualTo(PngSignature[i]), $"PNG シグネチャが違います: {path}");
            }

            // IHDR は先頭チャンクであることが PNG の仕様で決まっている（シグネチャ直後）
            Assert.That(
                bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R',
                Is.True, $"IHDR チャンクが見つかりません: {path}");

            var width = ReadUInt32BigEndian(bytes, 16);
            var height = ReadUInt32BigEndian(bytes, 20);

            Assert.That(width, Is.EqualTo((uint)expectedWidth), $"幅が {expectedWidth} ではありません: {path}");
            Assert.That(height, Is.EqualTo((uint)expectedHeight), $"高さが {expectedHeight} ではありません: {path}");
        }

        private static uint ReadUInt32BigEndian(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24)
                | ((uint)bytes[offset + 1] << 16)
                | ((uint)bytes[offset + 2] << 8)
                | bytes[offset + 3];
        }
    }
}
