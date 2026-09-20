using System;
using System.Collections.Generic;
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

        // ---- 刻みの厳密さ（レビュー8。現行構成では発火しないことをテストで固定する） ----

        /// <summary>
        /// ★★ <b>1要素の実時間は <c>floor(SampleRate * frameMs / 1000) / SampleRate</c> なのに、
        ///   ハンドルに載るのは公称の <c>frameMs</c>。</b> 割り切れないレートでは口が音からずれる
        ///   （11025Hz なら 0.23%、30秒の発話で約 70ms）。
        ///
        /// ★ <b>いま出会うレートはすべて割り切れるので発火しない。</b> このテストは
        ///   <b>その前提が崩れた瞬間に赤くする</b>ためにある —— 合成エンジンを差し替えたり
        ///   <c>DefaultFrameMs</c> を変えたりしたら、ここで気づく。
        /// </summary>
        [Test]
        public void EveryRateWeActuallyMeetDividesEvenly()
        {
            foreach (var rate in new[] { 16000, 22050, 24000, 44100, 48000 })
            {
                Assert.That(
                    (long)rate * LipSyncEnvelope.DefaultFrameMs % 1000L, Is.EqualTo(0L),
                    $"{rate}Hz が {LipSyncEnvelope.DefaultFrameMs}ms を割り切らない");
            }
        }

        /// <summary>
        /// ★ <b>割り切れないレートでの逸脱を、既知の限界として値ごと固定する。</b>
        ///   11025Hz の 20ms は 220.5 サンプルで、実装は floor して <b>220 サンプル
        ///   （＝19.955ms）</b>を1要素にする。0.2 秒 = 2205 サンプルなので <b>11 フレーム</b>
        ///   （厳密に 20ms なら 10 フレーム）。
        /// ★ <b>ずれる向きは安全側。</b> 実フレームが公称より短い → エンベロープが公称より速く進む
        ///   → 索引が小さくなる → <b>口が音より遅れる</b>。<c>lipSyncOffsetMs</c> の doc が
        ///   「口が音より先に動くより、遅れるほうが自然に見える」と書いている、その向き。
        /// </summary>
        [Test]
        public void FrameLengthIsFlooredToWholeSamples()
        {
            const int rate = 11025;

            var envelope = Envelope(WavBuilder.Build(
                Constant(rate / 5, 0.5f), WavBuilder.Encoding.Float32, rate));

            Assert.That(envelope.Length, Is.EqualTo(11));
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

        /// <summary>
        /// 極端な刻みでも落ちないこと（1要素 = 1サンプル未満 / 発話より長い刻み）。
        ///
        /// ★★ <b><c>int.MaxValue</c> まで見ること。</b> 以前は 1 と 1000 しか見ておらず、
        ///   <b>テスト名が実際の保証を上回っていた</b>（PR #74 のレビューの過程で判明）。
        ///   <c>SampleRate * frameMs / 1000</c> を <c>(int)</c> にキャストすると
        ///   24000Hz × <c>int.MaxValue</c> は <b>-24 に折り返し</b>、<c>new float[-24]</c> が
        ///   <c>OverflowException</c> になる。
        /// </summary>
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

            // 1フレームが発話全体より長い ＝ フレームは1つ
            var huge = Envelope(wav, int.MaxValue);
            Assert.That(huge, Is.Not.Null);
            Assert.That(huge.Length, Is.EqualTo(1));
            Assert.That(huge[0], Is.EqualTo(1f).Within(1e-5f));
        }

        /// <summary>
        /// ★★ <b>ヘッダの値は外から来る。</b> <c>TryReadHeader</c> は <c>channels != 0</c> と
        ///   <c>sampleRate &gt; 0</c> しか見ないので、壊れた WAV が 20億 Hz を名乗れる。
        ///   1フレームのサンプル数を入力の長さで頭打ちにしていないと、
        ///   <b>160MB をメインスレッドで確保する</b>（<c>Prepare</c> は <c>FetchAudioAsync</c> の継続）。
        /// </summary>
        [Test]
        public void AbsurdHeaderValuesDoNotAllocateBeyondTheInput()
        {
            // 240 サンプル（480 バイト）しか無いのに 20億 Hz を名乗る WAV
            var wav = WavBuilder.BuildRaw(new byte[480], 1, 16, 2000000000, 1);

            var envelope = Envelope(wav);

            Assert.That(envelope, Is.Not.Null);
            Assert.That(envelope.Length, Is.EqualTo(1));
        }

        // ---- BuildOrWarn（両方の ISpeechPlayer 実装が通る唯一の入り口） ----

        /// <summary>
        /// ★★ <b>この2本が、#58 が「絶対に守る」と宣言した規則を<u>プラットフォーム非依存に</u>
        ///   踏む唯一のテスト。</b> 以前は同じロジックが2実装に逐語コピーされていて、
        ///   「作れなくても <c>Prepare</c> を落とさない」を守るテストは
        ///   <c>#if UNITY_EDITOR_OSX</c> の Afplay 側にしか無かった（Linux CI では
        ///   <c>AudioClipPlayer</c> 側が1行も踏まれない）。
        /// ★ 警告は1回だけ。読めない WAV は<b>同じ形が続く</b>ので、毎回出すとログが洪水になり、
        ///   無音の原因を追う窓が埋まる。
        /// </summary>
        [Test]
        public void BuildOrWarnWarnsOnceForRepeatedFailures()
        {
            var wav = WavBuilder.BuildRaw(new byte[480], 1, 12, SampleRate, 1);
            WavHeader header;
            string headerError;
            Assert.That(WavDecoder.TryReadHeader(wav, out header, out headerError), Is.True, headerError);

            var messages = new List<string>();
            var warned = false;

            Assert.That(LipSyncEnvelope.BuildOrWarn(wav, header, FrameMs, ref warned, messages.Add), Is.Null);
            Assert.That(LipSyncEnvelope.BuildOrWarn(wav, header, FrameMs, ref warned, messages.Add), Is.Null);

            Assert.That(warned, Is.True);
            Assert.That(messages.Count, Is.EqualTo(1));
            // 理由（ビット深度）が残っていること。ここが消えると無音の原因が読めなくなる
            Assert.That(messages[0], Does.Contain("12"));
        }

        [Test]
        public void BuildOrWarnDoesNotWarnOnSuccess()
        {
            var wav = WavBuilder.Build(Constant(FrameSamples, 1f), WavBuilder.Encoding.Float32);
            WavHeader header;
            string headerError;
            Assert.That(WavDecoder.TryReadHeader(wav, out header, out headerError), Is.True, headerError);

            var messages = new List<string>();
            var warned = false;

            var envelope = LipSyncEnvelope.BuildOrWarn(wav, header, FrameMs, ref warned, messages.Add);

            Assert.That(envelope, Is.Not.Null);
            Assert.That(warned, Is.False);
            Assert.That(messages, Is.Empty);
        }

        /// <summary>★ <c>Warn</c> は誰も購読していないことがある（<c>MascotRunner.Start</c> の前）。</summary>
        [Test]
        public void BuildOrWarnDoesNotThrowWithoutASink()
        {
            var wav = WavBuilder.BuildRaw(new byte[480], 1, 12, SampleRate, 1);
            WavHeader header;
            string headerError;
            Assert.That(WavDecoder.TryReadHeader(wav, out header, out headerError), Is.True, headerError);

            var warned = false;
            Assert.That(LipSyncEnvelope.BuildOrWarn(wav, header, FrameMs, ref warned, null), Is.Null);
            Assert.That(warned, Is.True);
        }

        /// <summary>
        /// ★ <c>WavDecoder.TryReadSamples</c> / <c>TryReadSamplesInto</c> は <c>public</c> なので、
        ///   <c>TryReadHeader</c> を通していない範囲を渡されうる。<c>IndexOutOfRangeException</c> は
        ///   <c>Prepare</c> → <c>FetchAudioAsync</c> の fire-and-forget で<b>未観測のまま捨てられ、
        ///   head が黙って止まる</b>ので、<c>error</c> で返すこと。
        /// </summary>
        [Test]
        public void OutOfRangeReadsAreRejectedWithAReason()
        {
            var wav = WavBuilder.Build(Constant(16, 1f), WavBuilder.Encoding.Float32);

            float[] samples;
            string error;

            Assert.That(WavDecoder.TryReadSamples(wav, 0, wav.Length + 8, 3, 32, out samples, out error), Is.False);
            Assert.That(error, Is.Not.Null);

            Assert.That(WavDecoder.TryReadSamples(wav, -1, 4, 3, 32, out samples, out error), Is.False);
            Assert.That(error, Is.Not.Null);

            Assert.That(WavDecoder.TryReadSamples(wav, 0, -4, 3, 32, out samples, out error), Is.False);
            Assert.That(error, Is.Not.Null);

            Assert.That(WavDecoder.TryReadSamples(null, 0, 4, 3, 32, out samples, out error), Is.False);
            Assert.That(error, Is.Not.Null);
        }
    }
}
