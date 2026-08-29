using System;
using System.Collections.Generic;
using ChatterMascot.Audio;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class WavDecoderTests
    {
        /// <summary>
        /// 16bit PCM の WAV を組み立てる（合成エンジンが返すのと同じ形）。
        /// ★ 実体は <see cref="WavBuilder"/>。<b>ここに書き写さないこと</b>（#58 で1箇所に寄せた）。
        /// </summary>
        private static byte[] BuildWav(short[] samples, int sampleRate = 24000, ushort channels = 1)
        {
            return WavBuilder.Build(samples, sampleRate, channels);
        }

        [Test]
        public void DecodesSixteenBitPcm()
        {
            var wav = BuildWav(new short[] { 0, 16384, -16384, 32767 });
            string error;
            var clip = WavDecoder.Decode(wav, "test", out error);

            Assert.That(error, Is.Null);
            Assert.That(clip, Is.Not.Null);
            Assert.That(clip.channels, Is.EqualTo(1));
            Assert.That(clip.frequency, Is.EqualTo(24000));
            Assert.That(clip.samples, Is.EqualTo(4));

            var data = new float[4];
            clip.GetData(data, 0);
            Assert.That(data[0], Is.EqualTo(0f).Within(0.001f));
            Assert.That(data[1], Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(data[2], Is.EqualTo(-0.5f).Within(0.001f));
            Assert.That(data[3], Is.EqualTo(1f).Within(0.001f));

            UnityEngine.Object.DestroyImmediate(clip);
        }

        [Test]
        public void DecodesStereo()
        {
            var wav = BuildWav(new short[] { 0, 0, 16384, -16384 }, 48000, 2);
            string error;
            var clip = WavDecoder.Decode(wav, "test", out error);

            Assert.That(clip, Is.Not.Null, error);
            Assert.That(clip.channels, Is.EqualTo(2));
            Assert.That(clip.frequency, Is.EqualTo(48000));
            // samples はチャンネルあたりの数
            Assert.That(clip.samples, Is.EqualTo(2));

            UnityEngine.Object.DestroyImmediate(clip);
        }

        [Test]
        public void RejectsNonRiff()
        {
            string error;
            Assert.That(WavDecoder.Decode(new byte[] { 1, 2, 3, 4 }, "test", out error), Is.Null);
            Assert.That(error, Is.Not.Null);

            var notWave = new byte[16];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(notWave, 0);
            System.Text.Encoding.ASCII.GetBytes("XXXX").CopyTo(notWave, 8);
            Assert.That(WavDecoder.Decode(notWave, "test", out error), Is.Null);
        }

        [Test]
        public void RejectsNullOrEmpty()
        {
            string error;
            Assert.That(WavDecoder.Decode(null, "test", out error), Is.Null);
            Assert.That(WavDecoder.Decode(new byte[0], "test", out error), Is.Null);
        }

        /// <summary>data チャンクが無ければ読めない（無音として鳴らさず、失敗として扱う）</summary>
        [Test]
        public void RejectsMissingDataChunk()
        {
            var wav = BuildWav(new short[] { 1, 2 });
            // "data" を潰す
            var index = IndexOf(wav, "data");
            Assert.That(index, Is.GreaterThan(0));
            wav[index] = (byte)'X';

            string error;
            Assert.That(WavDecoder.Decode(wav, "test", out error), Is.Null);
            Assert.That(error, Does.Contain("data"));
        }

        /// <summary>
        /// ★ <c>data</c> の宣言サイズが <b>0</b> でも実体で測り直して読む。
        ///
        /// ストリーミングで書かれた WAV はここが 0 や <c>0xFFFFFFFF</c> になる。
        /// 0 を弾くと、合成側が data サイズを後追いで埋める書き方に変えただけで
        /// <b>全文が無音のままスキップされる</b>（AudioFailed → 1回リトライ →
        /// 「seq=N の音声を取れなかったので飛ばします」）。
        /// 参照実装（<c>core/src/player/audioPlayer.ts</c>）も両方を実体で測り直している。
        /// </summary>
        [Test]
        public void MeasuresZeroSizedDataChunk()
        {
            var wav = BuildWav(new short[] { 0, 16384, -16384, 32767 });
            OverwriteDataChunkSize(wav, 0);

            string error;
            var clip = WavDecoder.Decode(wav, "test", out error);

            Assert.That(clip, Is.Not.Null, error);
            Assert.That(clip.samples, Is.EqualTo(4));

            UnityEngine.Object.DestroyImmediate(clip);
        }

        /// <summary>★ <c>0xFFFFFFFF</c>（Int32 では -1）も同じく実体で測り直す</summary>
        [Test]
        public void MeasuresOversizedDataChunk()
        {
            var wav = BuildWav(new short[] { 0, 16384, -16384, 32767 });
            OverwriteDataChunkSize(wav, -1);

            string error;
            var clip = WavDecoder.Decode(wav, "test", out error);

            Assert.That(clip, Is.Not.Null, error);
            Assert.That(clip.samples, Is.EqualTo(4));

            UnityEngine.Object.DestroyImmediate(clip);
        }

        /// <summary>
        /// ★ <b><c>data</c> より手前に長さ 0 のチャンクがあっても走査が止まらないこと。</b>
        ///
        /// 実体での測り直しを<b>全チャンクに広げると</b>、長さ 0 のチャンク1つで
        /// <c>chunkSize</c> が「末尾まで」になり、<c>offset</c> が範囲外へ飛んで
        /// <b><c>data</c> に到達しないまま「data チャンクがありません」</b>になる。
        /// 測り直すのは <c>data</c> の分岐の中だけ、前進は宣言値で（写し元と同じ）。
        /// </summary>
        [Test]
        public void SkipsZeroSizedChunkBeforeData()
        {
            var wav = BuildWav(new short[] { 0, 16384 });
            var withEmptyList = new List<byte>();
            withEmptyList.AddRange(new ArraySegment<byte>(wav, 0, 12));                  // RIFF/WAVE
            withEmptyList.AddRange(System.Text.Encoding.ASCII.GetBytes("LIST"));
            withEmptyList.AddRange(BitConverter.GetBytes(0));                            // ★ 長さ 0
            withEmptyList.AddRange(new ArraySegment<byte>(wav, 12, wav.Length - 12));    // fmt + data

            string error;
            var clip = WavDecoder.Decode(withEmptyList.ToArray(), "test", out error);
            Assert.That(clip, Is.Not.Null, error);
            Assert.That(clip.samples, Is.EqualTo(2));

            UnityEngine.Object.DestroyImmediate(clip);
        }

        /// <summary>
        /// ★ <b>チャンク長が <c>int.MaxValue</c> 級でも例外にならないこと。</b>
        ///
        /// 前進は <c>body + declared</c> を int で計算するので、越える長さでは<b>負に折り返す</b>。
        /// すると <c>offset</c> が負のままループ条件を通り、<c>Encoding4</c> の
        /// <c>data[offset]</c> が <c>IndexOutOfRangeException</c> を投げる。
        /// <c>Decode</c> に try/catch は無く、呼び出し元の <c>FetchAudioAsync</c> は
        /// fire-and-forget なので、<b>例外は未観測のまま捨てられ、キューの head が黙って止まる</b>。
        ///
        /// ★ このテストは戻り値より<b>「投げないこと」の方が本体</b>。
        /// </summary>
        [Test]
        public void DoesNotOverflowOnHugeChunkSize()
        {
            var wav = BuildWav(new short[] { 0, 16384 });
            var withHugeChunk = new List<byte>();
            withHugeChunk.AddRange(new ArraySegment<byte>(wav, 0, 12));                  // RIFF/WAVE
            withHugeChunk.AddRange(System.Text.Encoding.ASCII.GetBytes("LIST"));
            withHugeChunk.AddRange(BitConverter.GetBytes(int.MaxValue));                 // ★ 溢れる長さ
            withHugeChunk.AddRange(new ArraySegment<byte>(wav, 12, wav.Length - 12));    // fmt + data

            string error;
            // 直る前はこの行が IndexOutOfRangeException を投げる
            var clip = WavDecoder.Decode(withHugeChunk.ToArray(), "test", out error);

            Assert.That(clip, Is.Null);
            Assert.That(error, Is.Not.Null);
        }

        /// <summary>実体で測り直しても中身が無ければ、読めなかったことにする</summary>
        [Test]
        public void RejectsTrulyEmptyDataChunk()
        {
            var wav = BuildWav(new short[0]);

            string error;
            Assert.That(WavDecoder.Decode(wav, "test", out error), Is.Null);
            Assert.That(error, Does.Contain("data"));
        }

        /// <summary>
        /// fmt より前に別のチャンクが挟まっても読める（LIST など。順序を決め打ちにしない）
        /// </summary>
        [Test]
        public void SkipsUnknownChunks()
        {
            var wav = BuildWav(new short[] { 0, 16384 });
            var withList = new List<byte>();
            withList.AddRange(new ArraySegment<byte>(wav, 0, 12));                    // RIFF/WAVE
            withList.AddRange(System.Text.Encoding.ASCII.GetBytes("LIST"));
            withList.AddRange(BitConverter.GetBytes(4));
            withList.AddRange(new byte[] { 1, 2, 3, 4 });
            withList.AddRange(new ArraySegment<byte>(wav, 12, wav.Length - 12));      // fmt + data

            string error;
            var clip = WavDecoder.Decode(withList.ToArray(), "test", out error);
            Assert.That(clip, Is.Not.Null, error);
            Assert.That(clip.samples, Is.EqualTo(2));

            UnityEngine.Object.DestroyImmediate(clip);
        }

        /// <summary><c>data</c> チャンクの宣言サイズだけを書き換える（中身はそのまま）。</summary>
        private static void OverwriteDataChunkSize(byte[] wav, int size)
        {
            var index = IndexOf(wav, "data");
            Assert.That(index, Is.GreaterThan(0));
            var bytes = BitConverter.GetBytes(size);
            for (var i = 0; i < 4; i++) wav[index + 4 + i] = bytes[i];
        }

        // ---- ヘッダだけを読む経路（再生の実体が AudioSource でなくなっても残る） ----

        /// <summary>
        /// 再生時間は fmt の byteRate から出す。参照実装（<c>core/src/player/audioPlayer.ts</c> の
        /// <c>wavDurationMs</c>）と同じ根拠にしてある。
        /// </summary>
        [Test]
        public void ReadsDurationFromByteRate()
        {
            // 24000Hz / 1ch / 16bit で 2400 サンプル = ちょうど 100ms
            var wav = BuildWav(new short[2400]);

            WavHeader header;
            string error;
            Assert.That(WavDecoder.TryReadHeader(wav, out header, out error), Is.True);

            Assert.That(error, Is.Null);
            Assert.That(header.DurationMs, Is.EqualTo(100));
            Assert.That(header.SampleRate, Is.EqualTo(24000));
            Assert.That(header.Channels, Is.EqualTo(1));
            Assert.That(header.BitsPerSample, Is.EqualTo(16));
            Assert.That(header.DataLength, Is.EqualTo(4800));
        }

        /// <summary>
        /// ★ byteRate が読めないときの 0 は「長さ 0」ではなく「不明」。
        ///
        /// 長さ 0 として期限を計算すると <c>0 * 2 + 5秒</c> になり、<b>すべての再生が
        /// 5秒で打ち切られる</b>。打ち切られた文は PlaybackFailed → ack に落ちるので
        /// サーバーのキューからも消えて二度と鳴らせない。呼び出し側は 0 を見たら
        /// 120秒のフォールバックに倒すこと。
        /// </summary>
        [Test]
        public void ReportsUnknownDurationWhenByteRateIsZero()
        {
            var wav = BuildWav(new short[2400]);
            // fmt チャンクの byteRate は先頭から 28 バイト目
            //   RIFF(4) + size(4) + WAVE(4) + "fmt "(4) + size(4) + format(2) + channels(2) + sampleRate(4)
            BitConverter.GetBytes(0).CopyTo(wav, 28);

            WavHeader header;
            string error;
            Assert.That(WavDecoder.TryReadHeader(wav, out header, out error), Is.True);

            Assert.That(error, Is.Null);
            Assert.That(header.DurationMs, Is.EqualTo(0));
            // 他の項目は読めている（byteRate だけが欠けた WAV でも再生自体はできる）
            Assert.That(header.SampleRate, Is.EqualTo(24000));
            Assert.That(header.DataLength, Is.EqualTo(4800));
        }

        /// <summary>
        /// 溢れる長さのチャンクは <see cref="WavDecoder.TryReadHeader"/> の側で打ち切る。
        ///
        /// ★ <see cref="WavDecoder.Decode"/> 経由の同じ回帰（<c>DoesNotOverflowOnHugeChunkSize</c>）と
        ///   重複して見えるが、こちらは <c>AudioClip.Create</c> を通らない。
        ///   <c>Disable Unity Audio</c> を入れて <c>AudioClip</c> が作れなくなっても、
        ///   走査の回帰はこのテストが守る。
        /// </summary>
        [Test]
        public void TryReadHeaderDoesNotOverflowOnHugeChunkSize()
        {
            var wav = BuildWav(new short[] { 0, 16384 });
            var withHugeChunk = new List<byte>();
            withHugeChunk.AddRange(new ArraySegment<byte>(wav, 0, 12));                  // RIFF/WAVE
            withHugeChunk.AddRange(System.Text.Encoding.ASCII.GetBytes("LIST"));
            withHugeChunk.AddRange(BitConverter.GetBytes(int.MaxValue));                 // ★ 溢れる長さ
            withHugeChunk.AddRange(new ArraySegment<byte>(wav, 12, wav.Length - 12));    // fmt + data

            WavHeader header;
            string error;
            // 直る前はこの行が IndexOutOfRangeException を投げる
            var ok = WavDecoder.TryReadHeader(withHugeChunk.ToArray(), out header, out error);

            Assert.That(ok, Is.False);
            Assert.That(error, Is.Not.Null);
        }

        private static int IndexOf(byte[] data, string tag)
        {
            for (var i = 0; i + tag.Length <= data.Length; i++)
            {
                var found = true;
                for (var j = 0; j < tag.Length; j++)
                {
                    if (data[i + j] != (byte)tag[j]) { found = false; break; }
                }
                if (found) return i;
            }
            return -1;
        }
    }
}
