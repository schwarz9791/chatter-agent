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
                // ★ 理由は WavDecoder 側の文言をそのまま使いたいので、空読みさせて受け取る
                float[] unused;
                WavDecoder.TryReadSamples(wav, header.DataOffset, 0, header.Format, header.BitsPerSample,
                    out unused, out error);
                return null;
            }

            // ★ **チャンネルを潰す。** 1フレームのサンプル数はチャンネル数を掛けた値で、
            //   インターリーブされたまま二乗和に入れて実要素数で割れば平均になる。
            // ★ **24000Hz/1ch に決め打ちしないこと。** ttsBaseUrl を VOICEVOX に向ければ
            //   別のレートになりうる（そのとき口が音に合わなくなるが、エラーは出ない）。
            var perChannel = (int)Math.Max(1L, (long)header.SampleRate * frameMs / 1000L);
            var frameSamples = perChannel * header.Channels;

            var totalSamples = header.DataLength / stride;
            if (totalSamples <= 0)
            {
                error = "サンプルがありません";
                return null;
            }

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
