using System;
using System.Collections.Generic;
using ChatterMascot.Audio;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class WavDecoderTests
    {
        /// <summary>16bit PCM の WAV を組み立てる（合成エンジンが返すのと同じ形）。</summary>
        private static byte[] BuildWav(short[] samples, int sampleRate = 24000, ushort channels = 1)
        {
            var dataBytes = samples.Length * 2;
            var bytes = new List<byte>();

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bytes.AddRange(BitConverter.GetBytes(36 + dataBytes));
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bytes.AddRange(BitConverter.GetBytes(16));
            bytes.AddRange(BitConverter.GetBytes((ushort)1));          // PCM
            bytes.AddRange(BitConverter.GetBytes(channels));
            bytes.AddRange(BitConverter.GetBytes(sampleRate));
            bytes.AddRange(BitConverter.GetBytes(sampleRate * channels * 2));
            bytes.AddRange(BitConverter.GetBytes((ushort)(channels * 2)));
            bytes.AddRange(BitConverter.GetBytes((ushort)16));

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            bytes.AddRange(BitConverter.GetBytes(dataBytes));
            foreach (var sample in samples) bytes.AddRange(BitConverter.GetBytes(sample));

            return bytes.ToArray();
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
