using System;
using System.Globalization;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// 設定 UI の値と、実装側のつまみとの写像。<b>すべて純粋関数</b>。
    ///
    /// ★ <b>ここに集めるのは「テストで固定したい算数」だけ。</b> どれも一見自明だが、
    ///   実際に踏むと症状が「なんとなく大きさが違う」「ロケールによってだけ壊れる」のように
    ///   気づきにくい形で出る。
    /// </summary>
    public static class SettingsMapping
    {
        /// <summary>
        /// <c>VrmStage.headroom</c> の出荷値。<b>写像の基準にする値</b>。
        /// ★ シーンにシリアライズされた値が実際の初期値なので、あちらを変えたらここも変えること。
        /// </summary>
        public const float DefaultHeadroom = 1.1f;

        /// <summary>UI の「キャラクターの大きさ」の範囲と刻み</summary>
        public const float ScaleMin = 0.5f;
        public const float ScaleMax = 2.0f;
        public const float ScaleStep = 0.1f;

        /// <summary>音量の範囲と刻み。★ <b>0.0〜2.0</b>（1.0 が上限ではない）</summary>
        public const float VolumeMin = 0.0f;
        public const float VolumeMax = 2.0f;
        public const float VolumeStep = 0.1f;

        /// <summary>
        /// 話速の範囲と刻み。★ <b>表示上のもので、値域の権威は core の <c>SPECS</c>。</b>
        /// ズレても <c>PATCH /v1/config</c> が 400 を返すだけで、黙って効かない値にはならない。
        /// </summary>
        public const float SpeedMin = 0.5f;
        public const float SpeedMax = 2.0f;
        public const float SpeedStep = 0.1f;

        /// <summary>
        /// UI の「大きさ」→ <c>VrmStage.headroom</c>。
        ///
        /// ★★ <b><c>headroom</c> は大きいほどキャラが小さい</b>（カメラを後ろへ下げる余白の係数）。
        ///   UI の「大きさ」とは<b>向きが逆</b>なので、素直に代入すると
        ///   スライダーを右に振るほどキャラが小さくなる。実装を読まないと気づけないので、
        ///   写像をここに出してテストで固定してある。
        ///
        /// ★ <b>0 で割らないこと。</b> <paramref name="scale"/> は範囲でクランプしてから使う。
        /// </summary>
        public static float HeadroomFor(float scale)
        {
            var clamped = Clamp(scale, ScaleMin, ScaleMax);
            return DefaultHeadroom / clamped;
        }

        /// <summary><see cref="HeadroomFor"/> の逆。既存の <c>headroom</c> を UI の値に読み替える</summary>
        public static float ScaleFor(float headroom)
        {
            if (!(headroom > 0f)) return 1f;
            return Clamp(DefaultHeadroom / headroom, ScaleMin, ScaleMax);
        }

        /// <summary>
        /// 刻みへ丸める。
        ///
        /// ★★ <b>C# 側でも丸めること。</b> スライダーから返ってくる float は
        ///   <c>0.7000000119</c> になりうる。そのまま保存すると <c>settings.json</c> にも
        ///   <c>config.json</c> にもその文字列が残り、次に開いたときスライダーが
        ///   刻みに乗らない位置から始まる。
        ///
        /// ★ <b>double で計算すること。</b> float のまま <c>2.0f / 0.1f</c> を割ると
        ///   19.999998 になり、丸めが 20 の手前へ落ちることがある。
        ///
        /// ★ <paramref name="step"/> が 0 以下なら丸めない（呼び出し側の設定ミスで値を壊さない）。
        /// </summary>
        public static float RoundToStep(float value, float step)
        {
            if (!(step > 0f)) return value;
            if (float.IsNaN(value) || float.IsInfinity(value)) return value;
            var rounded = Math.Round((double)value / step, MidpointRounding.AwayFromZero) * step;
            return (float)rounded;
        }

        /// <summary>範囲へ収める（<c>Mathf</c> を使わないので Runtime 以外からも呼べる）</summary>
        public static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>刻みに丸めてから範囲へ収める。<b>保存・送信の直前に必ず通す</b></summary>
        public static float Normalize(float value, float min, float max, float step)
        {
            return Clamp(RoundToStep(value, step), min, max);
        }

        /// <summary>
        /// 数値を文字列にする。
        ///
        /// ★★ <b><c>InvariantCulture</c> を忘れないこと。</b> 忘れると、ロケールによって
        ///   <c>0,5</c> になる。行き先は <c>settings.json</c>（次回の読み込みで失敗）と
        ///   <c>afplay -v</c> の引数（再生が失敗）と <c>PATCH /v1/config</c> のボディ（400）で、
        ///   <b>症状が3つとも別々の場所に出る</b>。
        ///
        /// ★ 末尾の 0 を落とす（<c>0.70</c> ではなく <c>0.7</c>）。0.1 刻みなので
        ///   小数第1位まであれば足りる。
        /// </summary>
        public static string Format(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// <see cref="Format"/> の逆。読めなければ <paramref name="fallback"/>。
        ///
        /// ★ <b><c>InvariantCulture</c> で読むこと。</b> 書くときだけ揃えても、
        ///   読むときにロケールが混ざれば同じところで壊れる。
        /// </summary>
        public static float Parse(string text, float fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            float value;
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return fallback;
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            return value;
        }

        /// <summary>
        /// <c>afplay</c> に <c>-v</c> を足すべきか。
        ///
        /// ★★ <b><c>&lt; 1</c> で判定しないこと。</b> 範囲が 0.0〜2.0 なので、
        ///   <c>&lt; 1</c> だと<b>大きくする側が黙って効かなくなる</b>。
        ///   等倍のときだけ引数を増やさない（＝ #76 より前の挙動をそのまま保つ）。
        ///
        /// ★ <b>float の裸の等値比較を書かないこと。</b> 0.1 刻みに丸めた後でも
        ///   <c>1.0f</c> ちょうどになる保証は無いので、刻みの半分を許容幅にする。
        /// </summary>
        public static bool NeedsVolumeArgument(float volume)
        {
            return Math.Abs(volume - 1f) > VolumeStep / 2f;
        }
    }
}
