using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>StreamingAssets/trayTemplate.png</c>（16×16）と <c>trayTemplate@2x.png</c>（32×32）が
    /// 実在し、寸法・中身・メタデータについて要求を満たしていること。
    ///
    /// ★★ <b>なぜ要るか（寸法）。</b> <c>CMLoadTemplateImage</c>（<c>CMStatusItem.m</c>）は @1x と @2x を
    ///   1つの <c>NSImage</c> に入れ、<b>両 rep の最小 pixel 寸法</b>をポイントとして全 rep の
    ///   size を揃える。片方の寸法がずれると、Retina でぼやけるか非 Retina で 2 倍の大きさに
    ///   描かれる。テンプレート画像の事故は「なんとなく変」で済んでしまい気づきにくいので、
    ///   寸法をテストで固定する。<c>MenuJsonTests</c> はパスの文字列を pass-through で見るだけで、
    ///   画像そのものの実在も寸法も見ていない——今この事故を止めるテストはリポジトリに無かった。
    ///
    /// ★★ <b>なぜ要るか（アルファ）。</b> <c>[image setTemplate:YES]</c> はテンプレート画像の
    ///   <b>アルファだけ</b>を形として使う（RGB は無視される）。この PR で実際に入れた最初の素材は
    ///   塗りつぶしのシルエット（内側まで不透明）で、メニューバーに黒い塊として出た。
    ///   寸法テストはこの失敗を1つも検出できない——寸法は合っていたため。
    ///   だから見るのは2つ: <b>中心画素が透明であること</b>（アイコンの内側はくり抜かれているはず）と、
    ///   <b>不透明画素の比率が高すぎないこと</b>（塗りつぶしを弾く）。
    ///
    /// ★ <b>閾値はきつく締めすぎないこと。</b> 素材を作り直すたびに比率そのものは変わる。
    ///   ここで弾きたいのは「線画ではなく塗りつぶしのシルエットだ」という形の違いであって、
    ///   特定のピクセル数ではない。
    ///
    /// ★★ <b>なぜ要るか（メタデータチャンク）。</b> この PR で実際に、編集ツール（Affinity Designer）が
    ///   書いた XMP（<c>iTXt</c>）と ICC プロファイル（<c>iCCP</c>）が PNG に残ったまま
    ///   公開リポジトリに入り、実名・作成時刻・オーサリングツールが漏れた。画素とは無関係に
    ///   増える種類のチャンクなので、寸法やアルファのテストでは検出できない。
    ///   許可チャンクの allowlist（<c>IHDR</c> / <c>IDAT</c> / <c>IEND</c>）に
    ///   無いものが1つでもあれば失敗させ、次に素材を差し替えたときの再発を防ぐ。
    ///
    /// ★★ <b><c>PLTE</c> / <c>tRNS</c>（パレット形式）は allowlist から外している。</b>
    ///   このデコーダは colour type 6（RGBA）しか読めないので、<c>colortype=3</c> の
    ///   パレット PNG を許可チャンクに含めても <c>DecodeRgbaPixels</c> がそもそも読めず、
    ///   「テスト側の都合」に見えるだけの失敗になる。**pngquant を通すと自然にここへ落ちる**
    ///   （減色して <c>PLTE</c>/<c>tRNS</c> を持つ形式に変わるため。実際 <c>AppIcon.png</c> は
    ///   pngquant で colour type 3 になっている）。パレットは metadata の除去では代替できない
    ///   ので、最初から受け付けない。
    ///
    /// ★ <c>StreamingAssets</c> の PNG は Unity がインポートしないので
    ///   <c>AssetDatabase.LoadAssetAtPath&lt;Texture2D&gt;</c> では読めない。
    ///   <c>File.ReadAllBytes</c> して PNG のチャンクを直接読む。
    /// </summary>
    [TestFixture]
    public sealed class TrayIconTests
    {
        // PNG シグネチャ（RFC 2083）。先頭8バイトが一致しなければ PNG ではない
        private static readonly byte[] PngSignature =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        };

        // ★ ここに無いチャンクは全部「編集ツールが埋め込んだ何か」、もしくはこのデコーダが
        //   読めないパレット形式（PLTE/tRNS）の疑いがある。IHDR/IEND は PNG の構造上必須、
        //   IDAT が画素データ。テキスト・時刻・ICC プロファイル・パレットなど、
        //   RGBA の画素表示に要らないものは1つも許可しない
        private static readonly HashSet<string> AllowedChunkTypes = new HashSet<string>
        {
            "IHDR", "IDAT", "IEND",
        };

        // ★ 「中心が塗りつぶされている（＝シルエット）」を弾くための上限。線画・輪郭主体の
        //   テンプレート画像なら十分下回る。素材を作り直すたびに実際の比率は動くので、
        //   ここは絶対値の当てはめではなく「塗りつぶしと線画を分ける」ための余裕を残した値
        private const double MaxOpaquePixelRatio = 0.35;

        // ★ 不透明とみなすアルファのしきい値。アンチエイリアスの縁を「不透明」に数えて
        //   誤検出しないよう、255 未満にも少し余裕を持たせる
        private const byte OpaqueAlphaThreshold = 200;

        [Test]
        public void TrayTemplateIs16By16()
        {
            var image = LoadPng("trayTemplate.png");
            Assert.That(image.Width, Is.EqualTo(16), "幅が 16 ではありません: trayTemplate.png");
            Assert.That(image.Height, Is.EqualTo(16), "高さが 16 ではありません: trayTemplate.png");
        }

        [Test]
        public void TrayTemplate2xIs32By32()
        {
            var image = LoadPng("trayTemplate@2x.png");
            Assert.That(image.Width, Is.EqualTo(32), "幅が 32 ではありません: trayTemplate@2x.png");
            Assert.That(image.Height, Is.EqualTo(32), "高さが 32 ではありません: trayTemplate@2x.png");
        }

        [Test]
        public void TrayTemplateCenterIsTransparent()
        {
            AssertCenterPixelTransparent("trayTemplate.png");
        }

        [Test]
        public void TrayTemplate2xCenterIsTransparent()
        {
            AssertCenterPixelTransparent("trayTemplate@2x.png");
        }

        [Test]
        public void TrayTemplateIsNotFilled()
        {
            AssertNotFilled("trayTemplate.png");
        }

        [Test]
        public void TrayTemplate2xIsNotFilled()
        {
            AssertNotFilled("trayTemplate@2x.png");
        }

        [Test]
        public void TrayTemplateHasNoMetadataChunks()
        {
            AssertNoMetadataChunks("trayTemplate.png");
        }

        [Test]
        public void TrayTemplate2xHasNoMetadataChunks()
        {
            AssertNoMetadataChunks("trayTemplate@2x.png");
        }

        private static void AssertCenterPixelTransparent(string fileName)
        {
            var image = LoadPng(fileName);
            var pixels = image.DecodeRgbaPixels();
            var centerIndex = (image.Height / 2 * image.Width + image.Width / 2) * 4;
            var alpha = pixels[centerIndex + 3];

            // setTemplate:YES はアルファだけを形として使うので、アイコンの内側
            // （くり抜かれているべき部分）が不透明だと塗りつぶしのシルエットに見える
            Assert.That(alpha, Is.EqualTo(0),
                $"中心画素が透明ではありません（塗りつぶしのシルエットの疑い）: {fileName}");
        }

        private static void AssertNotFilled(string fileName)
        {
            var image = LoadPng(fileName);
            var pixels = image.DecodeRgbaPixels();
            var totalPixels = image.Width * image.Height;
            var opaque = 0;
            for (var i = 0; i < totalPixels; i++)
            {
                if (pixels[i * 4 + 3] > OpaqueAlphaThreshold) opaque++;
            }

            var ratio = (double)opaque / totalPixels;
            Assert.That(ratio, Is.LessThan(MaxOpaquePixelRatio),
                $"不透明画素の比率が高すぎます（塗りつぶしのシルエットの疑い）: {fileName} "
                + $"opaque={opaque}/{totalPixels} ({ratio:P1})");
        }

        private static void AssertNoMetadataChunks(string fileName)
        {
            var image = LoadPng(fileName);
            var unexpected = image.ChunkTypes.Where(t => !AllowedChunkTypes.Contains(t)).Distinct().ToList();
            Assert.That(unexpected, Is.Empty,
                $"許可していない PNG チャンクが残っています: {fileName} [{string.Join(", ", unexpected)}]。"
                + "編集ツールが埋め込んだメタデータ（作成者名・作成時刻・ICC プロファイルなど）の疑いがあるので、"
                + "個人情報が漏れていないか確認したうえで、メタデータを保持しない書き出し方法で作り直してください");
        }

        private static PngImage LoadPng(string fileName)
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

            return PngImage.Parse(bytes, path);
        }

        /// <summary>
        /// 8bit RGBA・非インターレースの PNG だけを扱う最小限のデコーダ（テスト専用）。
        /// トレイ画像はどちらもこの形式で書き出されている前提。
        ///
        /// ★ <c>System.IO.Compression.DeflateStream</c> は raw DEFLATE のデコーダで、
        ///   zlib ヘッダ（2バイト）を自分では読み飛ばさない。<c>IDAT</c> を連結したバイト列の
        ///   先頭2バイトを飛ばしてから渡す。末尾4バイトの Adler-32 チェックサムは
        ///   検証しない —— DeflateStream は DEFLATE ストリームの終端で読み取りを止めるので、
        ///   末尾に何が残っていても読み取り結果に影響しない。
        /// </summary>
        private sealed class PngImage
        {
            public int Width;
            public int Height;
            public byte BitDepth;
            public byte ColorType;
            public byte InterlaceMethod;
            public List<string> ChunkTypes;
            private byte[] _idat;

            public static PngImage Parse(byte[] bytes, string path)
            {
                var image = new PngImage { ChunkTypes = new List<string>() };
                var idat = new List<byte>();
                var offset = 8;

                while (offset + 8 <= bytes.Length)
                {
                    var length = (int)ReadUInt32BigEndian(bytes, offset);
                    var type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                    var dataStart = offset + 8;

                    Assert.That(dataStart + length + 4, Is.LessThanOrEqualTo(bytes.Length),
                        $"PNG チャンクが壊れています（{type}）: {path}");

                    image.ChunkTypes.Add(type);

                    if (type == "IHDR")
                    {
                        image.Width = (int)ReadUInt32BigEndian(bytes, dataStart);
                        image.Height = (int)ReadUInt32BigEndian(bytes, dataStart + 4);
                        image.BitDepth = bytes[dataStart + 8];
                        image.ColorType = bytes[dataStart + 9];
                        image.InterlaceMethod = bytes[dataStart + 12];
                    }
                    else if (type == "IDAT")
                    {
                        idat.AddRange(new ArraySegment<byte>(bytes, dataStart, length));
                    }

                    offset = dataStart + length + 4; // +4 は CRC
                    if (type == "IEND") break;
                }

                image._idat = idat.ToArray();
                return image;
            }

            /// <summary>PNG のフィルタを解いて、行優先・RGBA8 の生ピクセル列を返す。</summary>
            public byte[] DecodeRgbaPixels()
            {
                Assert.That(BitDepth, Is.EqualTo((byte)8), "このデコーダは 8bit 深度の PNG のみ対応しています");
                Assert.That(ColorType, Is.EqualTo((byte)6),
                    $"このデコーダは RGBA（colour type 6）の PNG のみ対応しています（実際は colour type {ColorType}）。"
                    + "pngquant 等の減色ツールを通すとパレット形式（colour type 3）になり読めなくなります。"
                    + "画像編集ツールから RGBA のまま書き出し直し、メタデータを落としたいときは "
                    + "PNG の必須チャンク（IHDR/IDAT/IEND）だけを残す方法を使ってください"
                    + "（IDAT は触らないので画素は無劣化です。詳細は docs/mascot.md の「トレイ画像を差し替えるとき」）");
                // ★ Adam7 インターレースは非インターレース前提のデコード（下のフィルタ復元ループ）を
                //   黙って通すことがある（走査線あたりのバイト数の想定が崩れるだけで、7パスの
                //   合計バイト数が非インターレース1本分を上回れば例外にならない）。その場合
                //   IndexOutOfRangeException にはならず、別物のピクセル列がそのまま返って
                //   CenterIsTransparent / IsNotFilled がノイズを検査することになる。
                //   IHDR の interlace method を明示的に見て、非インターレースだけを通す
                Assert.That(InterlaceMethod, Is.EqualTo((byte)0), "このデコーダは非インターレースの PNG のみ対応しています");

                byte[] raw;
                using (var compressed = new MemoryStream(_idat, 2, _idat.Length - 2))
                using (var deflate = new DeflateStream(compressed, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    deflate.CopyTo(output);
                    raw = output.ToArray();
                }

                const int bytesPerPixel = 4;
                var stride = Width * bytesPerPixel;
                var pixels = new byte[Height * stride];
                var previousRow = new byte[stride];
                var pos = 0;

                for (var y = 0; y < Height; y++)
                {
                    var filterType = raw[pos];
                    pos++;

                    var currentRow = new byte[stride];
                    for (var x = 0; x < stride; x++)
                    {
                        var a = x >= bytesPerPixel ? currentRow[x - bytesPerPixel] : (byte)0;
                        var b = previousRow[x];
                        var c = x >= bytesPerPixel ? previousRow[x - bytesPerPixel] : (byte)0;
                        currentRow[x] = (byte)((raw[pos + x] + Predictor(filterType, a, b, c)) & 0xFF);
                    }

                    pos += stride;
                    Array.Copy(currentRow, 0, pixels, y * stride, stride);
                    previousRow = currentRow;
                }

                return pixels;
            }

            /// <summary>PNG フィルタ（0..4）の予測値。仕様（RFC 2083, §6）どおりの実装。</summary>
            private static int Predictor(byte filterType, byte a, byte b, byte c)
            {
                switch (filterType)
                {
                    case 0: return 0; // None
                    case 1: return a; // Sub
                    case 2: return b; // Up
                    case 3: return (a + b) / 2; // Average
                    case 4: // Paeth
                        var p = a + b - c;
                        var pa = Math.Abs(p - a);
                        var pb = Math.Abs(p - b);
                        var pc = Math.Abs(p - c);
                        if (pa <= pb && pa <= pc) return a;
                        return pb <= pc ? b : c;
                    default:
                        throw new InvalidDataException($"未知の PNG フィルタタイプです: {filterType}");
                }
            }
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
