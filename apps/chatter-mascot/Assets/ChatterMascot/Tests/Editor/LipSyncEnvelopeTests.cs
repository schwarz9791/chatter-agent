using System;
using ChatterMascot.Audio;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="LipSyncEnvelope"/>。WAV → 一定間隔ごとの RMS。
    ///
    /// ★ <b>ここが返す値は「生の RMS」。</b> ゲイン（cc-mascot の <c>rms * 4</c>）は
    ///   <c>MouthTracker</c> が掛ける。ここで焼くと Inspector から調整できなくなり、
    ///   二重適用の事故も起きる。
    ///
    /// ★ <b>失敗は <c>null</c>。例外にしない。</b> 呼び出し側（<c>ISpeechPlayer.Prepare</c>）は
    ///   <c>null</c> でもハンドルを返す契約で、そうしないと <c>AudioFailed</c> → skip + ack で
    ///   <b>サーバーのキューから物理削除されて二度と鳴らせない</b>。
    /// </summary>
    [TestFixture]
    public sealed class LipSyncEnvelopeTests
    {
        private const int SampleRate = 24000;
        private const int FrameMs = 20;

        /// <summary>24000Hz / 20ms なので 1フレーム = 480 サンプル（1ch のとき）</summary>
        private const int FrameSamples = SampleRate * FrameMs / 1000;

        private static float[] Envelope(byte[] wav, int frameMs = FrameMs)
        {
            WavHeader header;
            string headerError;
            Assert.That(WavDecoder.TryReadHeader(wav, out header, out headerError), Is.True, headerError);

            string error;
            return LipSyncEnvelope.Build(wav, header, frameMs, out error);
        }

        private static float[] Constant(int count, float value)
        {
            var samples = new float[count];
            for (var i = 0; i < count; i++) samples[i] = value;
            return samples;
        }

        /// <summary>+value / -value を交互に。<b>24bit の符号拡張と 8bit の非対称を踏むため</b></summary>
        private static float[] Alternating(int count, float value)
        {
            var samples = new float[count];
            for (var i = 0; i < count; i++) samples[i] = i % 2 == 0 ? value : -value;
            return samples;
        }

        [Test]
        public void SilenceIsAllZero()
        {
            var envelope = Envelope(WavBuilder.Build(
                new float[FrameSamples * 3], WavBuilder.Encoding.Pcm16));

            Assert.That(envelope, Is.Not.Null);
            Assert.That(envelope.Length, Is.EqualTo(3));
            foreach (var value in envelope) Assert.That(value, Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void FullScaleDirectCurrentIsOne()
        {
            // ★ float32 で書く。16bit だと最大が 32767/32768 = 0.99997 になって厳密比較できない
            var envelope = Envelope(WavBuilder.Build(
                Constant(FrameSamples * 2, 1f), WavBuilder.Encoding.Float32));

            Assert.That(envelope.Length, Is.EqualTo(2));
            foreach (var value in envelope) Assert.That(value, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void FullScaleSineIsRootHalf()
        {
            // 1フレームちょうどが1周期になるようにする（480 サンプル = 50Hz）
            var samples = new float[FrameSamples * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)Math.Sin(2.0 * Math.PI * (i % FrameSamples) / FrameSamples);
            }

            var envelope = Envelope(WavBuilder.Build(samples, WavBuilder.Encoding.Float32));

            foreach (var value in envelope)
            {
                Assert.That(value, Is.EqualTo(0.70710678f).Within(0.01f));
            }
        }

        /// <summary>
        /// ★ <b>ビット深度の分岐を <see cref="LipSyncEnvelope"/> へ書き写していないことの検査。</b>
        ///   ±0.5 はどの形式でも量子化誤差なしに表せるので、値がずれたら実装が違っている。
        ///   <c>WavDecoder.TryReadSamplesInto</c> を通っている限りここは自動的に揃う。
        /// </summary>
        [Test]
        public void EverySupportedFormatGivesTheSameEnvelope()
        {
            var samples = Alternating(FrameSamples, 0.5f);

            var encodings = new[]
            {
                WavBuilder.Encoding.Pcm8,
                WavBuilder.Encoding.Pcm16,
                WavBuilder.Encoding.Pcm24,
                WavBuilder.Encoding.Pcm32,
                WavBuilder.Encoding.Float32,
            };

            foreach (var encoding in encodings)
            {
                var envelope = Envelope(WavBuilder.Build(samples, encoding));
                Assert.That(envelope, Is.Not.Null, encoding.ToString());
                Assert.That(envelope.Length, Is.EqualTo(1), encoding.ToString());
                Assert.That(envelope[0], Is.EqualTo(0.5f).Within(0.005f), encoding.ToString());
            }
        }

        /// <summary>
        /// ★ <b>チャンネルは潰す。</b> 左だけ鳴っていても口は1つ。
        ///   L=1.0 / R=0.0 の RMS は <c>sqrt((1 + 0) / 2) = 0.707</c>。
        /// </summary>
        [Test]
        public void ChannelsAreCollapsed()
        {
            var samples = new float[FrameSamples * 2];
            for (var i = 0; i < samples.Length; i += 2)
            {
                samples[i] = 1f;      // L
                samples[i + 1] = 0f;  // R
            }

            var envelope = Envelope(WavBuilder.Build(
                samples, WavBuilder.Encoding.Float32, SampleRate, 2));

            Assert.That(envelope.Length, Is.EqualTo(1));
            Assert.That(envelope[0], Is.EqualTo(0.70710678f).Within(0.001f));
        }

        /// <summary>
        /// ★★ <b>末尾の端数フレームは実サンプル数で割ること。</b> フレーム長でゼロ埋めして割ると
        ///   最後のフレームだけ小さくなり、<b>語尾で口が閉じる</b>。半フレームぶんの直流 1.0 は
        ///   ゼロ埋め実装だと 0.707 になるので、この1本で確実に捕まる。
        /// </summary>
        [Test]
        public void TrailingPartialFrameIsNotDiluted()
        {
            var envelope = Envelope(WavBuilder.Build(
                Constant(FrameSamples + FrameSamples / 2, 1f), WavBuilder.Encoding.Float32));

            Assert.That(envelope.Length, Is.EqualTo(2));
            Assert.That(envelope[0], Is.EqualTo(1f).Within(1e-5f));
            Assert.That(envelope[1], Is.EqualTo(1f).Within(1e-5f));
        }

        /// <summary>
        /// ★ <b>24000Hz / 1ch に決め打ちしないこと。</b> <c>ttsBaseUrl</c> を VOICEVOX に
        ///   向ければ別のレートになりうる（そのとき口が音に合わなくなるが、エラーは出ない）。
        /// </summary>
        [Test]
        public void FrameCountFollowsTheHeader()
        {
            const int rate = 48000;
            const int perChannel = rate * FrameMs / 1000;  // 960

            var envelope = Envelope(WavBuilder.Build(
                Constant(perChannel * 2 * 3, 0.5f), WavBuilder.Encoding.Float32, rate, 2));

            Assert.That(envelope.Length, Is.EqualTo(3));
        }

        /// <summary>
        /// ★★ <b>#58 が守りたい経路そのもの。</b> ヘッダは読めるがサンプルが読めない WAV。
        ///   <c>Prepare</c> はこれでも成功しなければならない（→ <c>AfplaySpeechPlayerTests</c>）。
        /// </summary>
        [Test]
        public void UnsupportedBitDepthReturnsNull()
        {
            var wav = WavBuilder.BuildRaw(new byte[240], 1, 12, SampleRate, 1);

            WavHeader header;
            string headerError;
            Assert.That(WavDecoder.TryReadHeader(wav, out header, out headerError), Is.True, headerError);

            string error;
            Assert.That(LipSyncEnvelope.Build(wav, header, FrameMs, out error), Is.Null);
            Assert.That(error, Is.Not.Null);
            Assert.That(error, Does.Contain("12"));
        }

        [Test]
        public void UnsupportedFormatReturnsNull()
        {
            // A-law（format = 6）。ヘッダは読めるがサンプルは読めない
            var wav = WavBuilder.BuildRaw(new byte[240], 6, 8, SampleRate, 1);

            WavHeader header;
            string headerError;
            Assert.That(WavDecoder.TryReadHeader(wav, out header, out headerError), Is.True, headerError);

            string error;
            Assert.That(LipSyncEnvelope.Build(wav, header, FrameMs, out error), Is.Null);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void RejectsNullWavAndEmptyHeader()
        {
            var header = default(WavHeader);

            string error;
            Assert.That(LipSyncEnvelope.Build(null, header, FrameMs, out error), Is.Null);
            Assert.That(error, Is.Not.Null);

            var wav = WavBuilder.Build(Constant(FrameSamples, 1f), WavBuilder.Encoding.Float32);
            Assert.That(LipSyncEnvelope.Build(wav, header, FrameMs, out error), Is.Null);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void RejectsNonPositiveFrameMs()
        {
            var wav = WavBuilder.Build(Constant(FrameSamples, 1f), WavBuilder.Encoding.Float32);

            Assert.That(Envelope(wav, 0), Is.Null);
            Assert.That(Envelope(wav, -1), Is.Null);
        }

        /// <summary>極端な刻みでも落ちないこと（1要素 = 1サンプル未満 / 発話より長い刻み）。</summary>
        [Test]
        public void ExtremeFrameSizesDoNotThrow()
        {
            var wav = WavBuilder.Build(Constant(FrameSamples, 1f), WavBuilder.Encoding.Float32);

            var fine = Envelope(wav, 1);
            Assert.That(fine, Is.Not.Null);
            Assert.That(fine.Length, Is.EqualTo(20));

            var coarse = Envelope(wav, 1000);
            Assert.That(coarse, Is.Not.Null);
            Assert.That(coarse.Length, Is.EqualTo(1));
            Assert.That(coarse[0], Is.EqualTo(1f).Within(1e-5f));
        }
    }
}
