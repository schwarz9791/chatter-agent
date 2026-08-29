using System;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// WAV から振幅エンベロープ（一定間隔ごとの RMS）を作る。<b>純粋。</b>
    ///
    /// ★ <b>ここで失敗しても発話を落としてはいけない。</b> 呼び出し側（各
    ///   <c>ISpeechPlayer.Prepare</c>）は <c>null</c> を受けても <b>ハンドルを返すこと</b>。
    ///   <c>Prepare</c> が <c>null</c> を返すと <c>AudioFailed</c> → skip + ack となり、
    ///   <b>サーバーのキューから物理削除されて二度と鳴らせない</b> ——
    ///   リップシンクの都合で発話を落とすのは本末転倒。
    ///
    /// ★ <b>サンプルの読み出しは <see cref="WavDecoder.TryReadSamplesInto"/> に任せる。</b>
    ///   8/16/24/32bit PCM と IEEE float32 の分岐（特に 24bit の符号拡張）を
    ///   ここへ書き写さないこと。独立実装が2つあると、片方だけ直したときに黙ってズレる。
    /// </summary>
    public static class LipSyncEnvelope
    {
        /// <summary>
        /// 既定の刻み。
        ///
        /// ★ <b>表示（30fps = 33.3ms）と割り切れないのは承知のうえ。</b> 点サンプリングすると
        ///   4割のフレームを読み飛ばすので、読む側（<c>SpeakingSet.Mouth</c>）が
        ///   <b>前フレームからの区間の最大値</b>を取ることで吸収する。
        /// ★ 30秒の発話で 1500 要素 = 6KB。孤児込みで同時に10本あっても無視できる。
        /// </summary>
        public const int DefaultFrameMs = 20;

        /// <summary>
        /// 読めたら 1要素 = <paramref name="frameMs"/> の RMS 列。<b>読めなければ <c>null</c></b>。
        /// </summary>
        /// <param name="header"><see cref="WavDecoder.TryReadHeader"/> 済みのもの（もう一度走査しない）</param>
        public static float[] Build(byte[] wav, in WavHeader header, int frameMs, out string error)
        {
            error = null;

            if (wav == null)
            {
                error = "WAV がありません";
                return null;
            }
            if (frameMs <= 0)
            {
                error = $"frameMs が不正です ({frameMs})";
                return null;
            }
            if (header.Channels == 0 || header.SampleRate <= 0 || header.DataLength <= 0)
            {
                error = "ヘッダが読めていません";
                return null;
            }

            var stride = WavDecoder.BytesPerSample(header.Format, header.BitsPerSample);
            if (stride == 0)
            {
                // ★ 文言は WavDecoder と共有する（「対応していないフォーマット」と
                //   「対応していないビット深度」の区別が、無音の原因を追うときに要る）
                error = WavDecoder.DescribeUnsupported(header.Format, header.BitsPerSample);
                return null;
            }

            var totalSamples = header.DataLength / stride;
            if (totalSamples <= 0)
            {
                error = "サンプルがありません";
                return null;
            }

            // ★ **チャンネルを潰す。** 1フレームのサンプル数はチャンネル数を掛けた値で、
            //   インターリーブされたまま二乗和に入れて実要素数で割れば平均になる。
            // ★ **24000Hz/1ch に決め打ちしないこと。** ttsBaseUrl を VOICEVOX に向ければ
            //   別のレートになりうる（そのとき口が音に合わなくなるが、エラーは出ない）。
            // ★★ **1フレームのサンプル数を入力の長さで頭打ちにすること。** SampleRate も
            //   Channels も frameMs も**外から来る値**で、TryReadHeader は
            //   `channels != 0` / `sampleRate > 0` しか見ていない。頭打ちにしないと:
            //     - 壊れた WAV が sampleRate = 20億 を名乗るだけで **160MB をメインスレッドで確保**
            //     - frameMs = int.MaxValue は (int) キャストで**負に折り返し**、
            //       new float[負] が OverflowException
            //   1フレームが発話全体より長いときフレームは1つで、読むのは totalSamples 要素だけ
            //   なので、頭打ちにしても結果は変わらない。
            var perChannel = Math.Max(1L, (long)header.SampleRate * frameMs / 1000L);
            if (perChannel > totalSamples) perChannel = totalSamples;
            var frameSamples = (int)Math.Max(1L, Math.Min(perChannel * header.Channels, (long)totalSamples));

            // ★ 端数のフレームも1つと数える（切り上げ）。落とすと語尾が消える
            var frames = (totalSamples + frameSamples - 1) / frameSamples;
            var envelope = new float[frames];

            // ★ **発話ぜんぶぶんの float[] を確保しない**（→ TryReadSamplesInto の doc）
            var scratch = new float[frameSamples];

            for (var frame = 0; frame < frames; frame++)
            {
                var first = (long)frame * frameSamples;
                var take = (int)Math.Min(frameSamples, totalSamples - first);

                int written;
                if (!WavDecoder.TryReadSamplesInto(
                        wav, header.DataOffset + (int)(first * stride), take * stride,
                        header.Format, header.BitsPerSample, scratch, out written, out error))
                {
                    return null;
                }

                double sum = 0.0;
                for (var i = 0; i < written; i++) sum += (double)scratch[i] * scratch[i];

                // ★ **端数フレームは実際のサンプル数で割ること。** ゼロ埋めして frameSamples で
                //   割ると最後のフレームだけ小さくなり、**語尾で口が閉じる**
                envelope[frame] = written > 0 ? (float)Math.Sqrt(sum / written) : 0f;
            }

            return envelope;
        }
    }
}
