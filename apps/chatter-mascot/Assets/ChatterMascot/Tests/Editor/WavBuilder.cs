using System;
using System.Collections.Generic;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// テスト用の WAV 組み立て。<b>ここ1箇所に集める。</b>
    ///
    /// ★ <b>各テストに private な <c>BuildWav</c> を持たせないこと。</b> #58 の時点で
    ///   <c>WavDecoderTests</c> と <c>AfplaySpeechPlayerTests</c> に同じものが2本あった。
    ///   独立実装が増えると、片方だけ直したときに黙ってズレる（<c>VrmCharacter.HasBindings</c> /
    ///   <c>VrmStage.MeasureBounds</c> を <c>public static</c> にしてあるのと同じ理由）。
    ///
    /// ★ <b><c>#if UNITY_EDITOR_OSX</c> で囲まないこと。</b> <c>AfplaySpeechPlayerTests</c> は
    ///   囲まれているが、それは呼び出し側の都合。ここを囲むと、囲まれていないテストから
    ///   使えなくなる。
    ///
    /// ★ <b>量子化は <c>WavDecoder</c> の逆変換に厳密に合わせること。</b> ずれると
    ///   「デコーダが正しいのにテストが落ちる」か、その逆が起きる。
    /// </summary>
    internal static class WavBuilder
    {
        internal enum Encoding
        {
            /// <summary>8bit PCM。<b>符号なし</b>（128 が無音）で分解能は 1/128</summary>
            Pcm8,
            Pcm16,
            Pcm24,
            Pcm32,
            Float32,
        }

        private const ushort FormatPcm = 1;
        private const ushort FormatIeeeFloat = 3;

        /// <summary>正規化振幅（-1..1）から作る。</summary>
        internal static byte[] Build(
            float[] interleaved, Encoding encoding, int sampleRate = 24000, ushort channels = 1)
        {
            var format = encoding == Encoding.Float32 ? FormatIeeeFloat : FormatPcm;
            var bits = BitsOf(encoding);

            var data = new List<byte>(interleaved.Length * (bits / 8));
            foreach (var sample in interleaved) Append(data, sample, encoding);

            return BuildRaw(data.ToArray(), format, bits, sampleRate, channels);
        }

        /// <summary>16bit PCM。<c>WavDecoderTests</c> が使っていた形をそのまま残したもの。</summary>
        internal static byte[] Build(short[] samples, int sampleRate = 24000, ushort channels = 1)
        {
            var data = new List<byte>(samples.Length * 2);
            foreach (var sample in samples) data.AddRange(BitConverter.GetBytes(sample));
            return BuildRaw(data.ToArray(), FormatPcm, 16, sampleRate, channels);
        }

        /// <summary>
        /// 生のバイト列とフォーマットを指定して作る。
        ///
        /// ★ <b>非対応の組み合わせを作れることが要点。</b> <c>bitsPerSample = 12</c> の PCM は
        ///   <c>TryReadHeader</c> を通って <c>TryReadSamples</c> だけが落ちる ——
        ///   「エンベロープが作れなくても発話は落とさない」経路を踏むのにこれが要る。
        /// </summary>
        internal static byte[] BuildRaw(
            byte[] data, ushort format, ushort bitsPerSample, int sampleRate, ushort channels)
        {
            var blockAlign = (ushort)Math.Max(1, channels * (bitsPerSample / 8));
            var byteRate = sampleRate * blockAlign;

            var bytes = new List<byte>(44 + data.Length);

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bytes.AddRange(BitConverter.GetBytes(36 + data.Length));
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bytes.AddRange(BitConverter.GetBytes(16));
            bytes.AddRange(BitConverter.GetBytes(format));
            bytes.AddRange(BitConverter.GetBytes(channels));
            bytes.AddRange(BitConverter.GetBytes(sampleRate));
            bytes.AddRange(BitConverter.GetBytes(byteRate));
            bytes.AddRange(BitConverter.GetBytes(blockAlign));
            bytes.AddRange(BitConverter.GetBytes(bitsPerSample));

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            bytes.AddRange(BitConverter.GetBytes(data.Length));
            bytes.AddRange(data);

            return bytes.ToArray();
        }

        internal static ushort BitsOf(Encoding encoding)
        {
            switch (encoding)
            {
                case Encoding.Pcm8: return 8;
                case Encoding.Pcm16: return 16;
                case Encoding.Pcm24: return 24;
                default: return 32;
            }
        }

        private static void Append(List<byte> into, float sample, Encoding encoding)
        {
            switch (encoding)
            {
                case Encoding.Pcm8:
                    // WavDecoder は (v - 128) / 128 で戻す。1.0 は 256 に届かないので 255 で頭打ち
                    into.Add((byte)Clamp(Math.Round(sample * 128.0) + 128.0, 0, 255));
                    break;
                case Encoding.Pcm16:
                    into.AddRange(BitConverter.GetBytes(
                        (short)Clamp(Math.Round(sample * 32768.0), short.MinValue, short.MaxValue)));
                    break;
                case Encoding.Pcm24:
                {
                    var value = (int)Clamp(Math.Round(sample * 8388608.0), -8388608, 8388607);
                    into.Add((byte)(value & 0xFF));
                    into.Add((byte)((value >> 8) & 0xFF));
                    into.Add((byte)((value >> 16) & 0xFF));
                    break;
                }
                case Encoding.Pcm32:
                    into.AddRange(BitConverter.GetBytes(
                        (int)Clamp(Math.Round(sample * 2147483648.0), int.MinValue, int.MaxValue)));
                    break;
                default:
                    into.AddRange(BitConverter.GetBytes(sample));
                    break;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }
    }
}
