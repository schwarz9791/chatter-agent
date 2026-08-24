using System;
using UnityEngine;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// サーバーから取った WAV を <see cref="AudioClip"/> にする。
    ///
    /// ★ <b>ストリーム再生にしないこと</b>（契約）。URL から直接鳴らすと、ack でキューが
    ///   消えた瞬間に途中で切れる。参照実装（Node の player）は一時ファイルに落としてから
    ///   <c>afplay</c> に渡すが、Unity は <see cref="AudioClip"/> をメモリに持てるので
    ///   <b>ファイルは要らない</b>。「先に全部受け取ってから鳴らす」という趣旨は同じ。
    ///
    /// 合成エンジン（AivisSpeech）が返すのは 16bit PCM だが、読み手を決め打ちにしない。
    /// </summary>
    public static class WavDecoder
    {
        private const ushort FormatPcm = 1;
        private const ushort FormatIeeeFloat = 3;
        private const ushort FormatExtensible = 0xFFFE;

        /// <summary>
        /// 読めたら <see cref="AudioClip"/>、読めなければ <c>null</c>（呼び出し側が転送失敗として扱う）。
        /// </summary>
        public static AudioClip Decode(byte[] wav, string name, out string error)
        {
            error = null;
            if (wav == null || wav.Length < 12)
            {
                error = "WAV が短すぎます";
                return null;
            }

            if (!Matches(wav, 0, "RIFF") || !Matches(wav, 8, "WAVE"))
            {
                error = "RIFF/WAVE ヘッダがありません";
                return null;
            }

            ushort format = 0;
            ushort channels = 0;
            var sampleRate = 0;
            ushort bitsPerSample = 0;
            var dataOffset = -1;
            var dataLength = 0;

            // チャンクを順に走査する。fmt と data の順序を決め打ちにしない
            var offset = 12;
            while (offset + 8 <= wav.Length)
            {
                var chunkId = Encoding4(wav, offset);
                var declared = BitConverter.ToInt32(wav, offset + 4);
                var body = offset + 8;
                // ループ条件（offset + 8 <= wav.Length）から body <= wav.Length が保証されるので、
                // available は必ず 0 以上（下の `declared < 0` は別の話で、前進できるかの検査）
                var available = wav.Length - body;

                if (chunkId == "data")
                {
                    dataOffset = body;

                    // ★ **実体で測り直すのは data のときだけ。**
                    //   ストリーミングで書かれた WAV は data のサイズが 0 や 0xFFFFFFFF
                    //   （Int32 では -1）のことがある。参照実装も data の分岐の中でだけ
                    //   測り直している（core/src/player/audioPlayer.ts）。
                    // ★ <b>0 を弾かないこと。</b> 合成側が data サイズを後追いで埋める書き方に
                    //   変えただけで、全フレームが「data チャンクがありません」になり、
                    //   **1文も鳴らないまま無言でスキップされる**（AudioFailed → 1回リトライ →
                    //   「seq=N の音声を取れなかったので飛ばします」）。
                    // ★ <b>この測り直しを全チャンクに広げないこと。</b> 広げると、
                    //   <b>data より手前に長さ 0 のチャンク（LIST / fact など）が1つあるだけで</b>
                    //   走査が末尾まで飛び、data に到達しないまま「data チャンクがありません」になる。
                    var usable = declared > 0 && declared <= available;
                    dataLength = usable ? declared : available;

                    // 末尾まで測ったなら、その先にチャンクは残っていない
                    if (!usable) break;
                }
                else if (chunkId == "fmt " && declared >= 16 && body + 16 <= wav.Length)
                {
                    format = BitConverter.ToUInt16(wav, body);
                    channels = BitConverter.ToUInt16(wav, body + 2);
                    sampleRate = BitConverter.ToInt32(wav, body + 4);
                    bitsPerSample = BitConverter.ToUInt16(wav, body + 14);

                    // WAVE_FORMAT_EXTENSIBLE は SubFormat の先頭2バイトが実体
                    if (format == FormatExtensible && declared >= 26 && body + 26 <= wav.Length)
                    {
                        format = BitConverter.ToUInt16(wav, body + 24);
                    }
                }

                // ★ **前進は宣言値で行う**（data で測り直した値ではない）。写し元も同じ。
                //   長さ 0 のチャンクはここで 8 バイトだけ進み、走査が続く。
                // ★ 負の長さ（0xFFFFFFFF）では前進できない。offset が戻ると走査が終わらないので、
                //   ここで打ち切る（data なら上で測り直して break 済み）。
                if (declared < 0) break;
                // チャンクは2バイト境界に整列する
                offset = body + declared + (declared % 2);
            }

            if (channels == 0 || sampleRate <= 0 || bitsPerSample == 0)
            {
                error = "fmt チャンクが読めません";
                return null;
            }
            if (dataOffset < 0)
            {
                error = "data チャンクがありません";
                return null;
            }
            // 実体で測り直しても 0 なら、本当に中身が無い
            if (dataLength <= 0)
            {
                error = "data チャンクが空です";
                return null;
            }

            float[] samples;
            if (!TryReadSamples(wav, dataOffset, dataLength, format, bitsPerSample, out samples, out error))
            {
                return null;
            }

            var perChannel = samples.Length / channels;
            if (perChannel <= 0)
            {
                error = "サンプルが空です";
                return null;
            }

            var clip = AudioClip.Create(name, perChannel, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static bool TryReadSamples(
            byte[] wav, int offset, int length, ushort format, ushort bitsPerSample,
            out float[] samples, out string error)
        {
            samples = null;
            error = null;

            if (format == FormatIeeeFloat && bitsPerSample == 32)
            {
                var count = length / 4;
                samples = new float[count];
                for (var i = 0; i < count; i++) samples[i] = BitConverter.ToSingle(wav, offset + i * 4);
                return true;
            }

            if (format != FormatPcm)
            {
                error = $"対応していない WAV フォーマットです (format={format}, bits={bitsPerSample})";
                return false;
            }

            switch (bitsPerSample)
            {
                case 8:
                {
                    // 8bit PCM は符号なし（0..255、128 が無音）
                    samples = new float[length];
                    for (var i = 0; i < length; i++) samples[i] = (wav[offset + i] - 128) / 128f;
                    return true;
                }
                case 16:
                {
                    var count = length / 2;
                    samples = new float[count];
                    for (var i = 0; i < count; i++)
                    {
                        samples[i] = BitConverter.ToInt16(wav, offset + i * 2) / 32768f;
                    }
                    return true;
                }
                case 24:
                {
                    var count = length / 3;
                    samples = new float[count];
                    for (var i = 0; i < count; i++)
                    {
                        var at = offset + i * 3;
                        var value = wav[at] | (wav[at + 1] << 8) | (wav[at + 2] << 16);
                        // 24bit の符号拡張
                        if ((value & 0x800000) != 0) value = (int)(value | 0xFF000000);
                        samples[i] = value / 8388608f;
                    }
                    return true;
                }
                case 32:
                {
                    var count = length / 4;
                    samples = new float[count];
                    for (var i = 0; i < count; i++)
                    {
                        samples[i] = BitConverter.ToInt32(wav, offset + i * 4) / 2147483648f;
                    }
                    return true;
                }
                default:
                    error = $"対応していないビット深度です ({bitsPerSample}bit)";
                    return false;
            }
        }

        private static bool Matches(byte[] data, int offset, string tag)
        {
            if (offset + tag.Length > data.Length) return false;
            for (var i = 0; i < tag.Length; i++)
            {
                if (data[offset + i] != (byte)tag[i]) return false;
            }
            return true;
        }

        private static string Encoding4(byte[] data, int offset)
        {
            return new string(new[]
            {
                (char)data[offset], (char)data[offset + 1], (char)data[offset + 2], (char)data[offset + 3],
            });
        }
    }
}
