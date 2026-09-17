using System;
using System.Globalization;
using UnityEngine;

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
        /// <summary>UI の「キャラクターの大きさ」の範囲と刻み。<b>ウィンドウの倍率</b>（→ <see cref="WindowSizeFor"/>）</summary>
        public const float ScaleMin = 0.5f;
        public const float ScaleMax = 2.0f;
        public const float ScaleStep = 0.1f;

        /// <summary>
        /// 音量の範囲と刻み。<b>0.0〜1.0</b>（画面には <b>0〜100%</b> で出る。
        /// → <see cref="SettingDisplay.Percent"/>）。
        ///
        /// ★★ <b>1.0 より上へ戻さないこと。</b> 1.0 超えが効くのは macOS
        ///   （<c>afplay -v</c>）だけで、Android の <see cref="UnityEngine.AudioSource.volume"/> は
        ///   <b>Unity 側で 0〜1 にクランプされる</b>（<c>AudioClipPlayer.CopySettings</c> は
        ///   そのクランプ後の値を写す）。<c>settings.json</c> は Android と共有するので、
        ///   <b>プラットフォームによって意味の変わる範囲を持たせない</b> ——
        ///   大きくしたいなら <c>AudioMixer</c> が要るが、それは<b>両方で効く形にしてから</b>入れる。
        ///
        /// ★ 刻みを細かくしたければ <see cref="VolumeStep"/> だけ変えればよい。
        ///   スライダーの目盛りも <c>settings.json</c> の丸めも <c>-v</c> の許容幅も追従する。
        /// </summary>
        public const float VolumeMin = 0.0f;
        public const float VolumeMax = 1.0f;
        public const float VolumeStep = 0.1f;

        /// <summary>
        /// 話速の範囲と刻み。★ <b>表示上のもので、値域の権威は core の <c>SPECS</c>。</b>
        /// ズレても <c>PATCH /v1/config</c> が 400 を返すだけで、黙って効かない値にはならない。
        /// </summary>
        public const float SpeedMin = 0.5f;
        public const float SpeedMax = 2.0f;
        public const float SpeedStep = 0.1f;

        /// <summary>
        /// Android XR の空間固定パラメータの範囲と既定値（→ <see cref="MascotSettings.XrScale"/> ほかの doc）。
        ///
        /// ★★ <b><see cref="ScaleMin"/> / <see cref="ScaleMax"/>（デスクトップのウィンドウ倍率）とは
        ///   別物。</b> 混同しないよう、こちらは必ず <c>Xr</c> を頭に付けて区別する。
        /// ★ 刻み（<c>Step</c>）は持たない。デスクトップの設定パネルにスライダーを出さないので、
        ///   刻みへ丸める理由が無い（→ <see cref="SettingsJson"/> の <c>xr</c> 読み取り）。
        /// ★ 既定は「机の上のミニチュアを、正面の画面を避けた右側に」置く。グラスの表示視野は
        ///   ヘッドセットより狭いので、方位と足元の深さは<b>起動時の正面を向いたまま全身が視野に
        ///   収まる</b>範囲に留める（外すと、描けているのに視野の縁で切れて見えない）。
        /// </summary>
        public const float XrScaleMin = 0.05f;
        public const float XrScaleMax = 1.0f;
        public const float XrDefaultScale = 0.18f;

        public const float XrDistanceMin = 0.2f;
        public const float XrDistanceMax = 5.0f;
        public const float XrDefaultDistance = 0.6f;

        public const float XrAzimuthMin = -180f;
        public const float XrAzimuthMax = 180f;
        public const float XrDefaultAzimuth = 20f;

        public const float XrFeetBelowEyeMin = -1.0f;
        public const float XrFeetBelowEyeMax = 2.0f;
        public const float XrDefaultFeetBelowEye = 0.2f;

        /// <summary>
        /// UI の「大きさ」→ <b>ウィンドウの大きさ</b>（ポイント）。
        ///
        /// ★★ <b><c>VrmStage.headroom</c> を動かさないこと。</b> あれはカメラを後ろへ下げる
        ///   余白の係数で、1 を下回ると<b>モデルが画面からはみ出す</b>（実機で頭と足が
        ///   対称に欠けた）。ウィンドウを変えれば <c>VrmStage</c> が
        ///   <c>Screen.width/height</c> の変化を毎フレーム見て**自動で収め直す**ので、
        ///   触るべきなのは窓の方。
        ///
        /// ★ <b>基準の大きさは引数で受ける。</b> 出荷値を持っているのは
        ///   <c>Desktop/WindowGeometry.cs</c> で、ここに書き写すと
        ///   「ウィンドウの大きさが決まる場所」がまた1つ増える（→ <c>docs/mascot.md</c>）。
        ///
        /// ★ 縦横を同じ倍率で掛ける（アスペクト比を保つ）。
        /// </summary>
        public static void WindowSizeFor(
            float scale, float baseWidth, float baseHeight, out float width, out float height)
        {
            var clamped = Clamp(RoundToStep(scale, ScaleStep), ScaleMin, ScaleMax);
            width = baseWidth * clamped;
            height = baseHeight * clamped;
        }

        /// <summary>
        /// <see cref="WindowSizeFor"/> の逆。**いまのウィンドウの大きさ**を倍率に読み替える。
        ///
        /// ★★ <b>倍率を <c>settings.json</c> に持たないための関数。</b> ウィンドウの大きさは
        ///   既に <c>window.json</c> が持っているので、両方に持つと権威が2つになる
        ///   （ユーザーが窓を直接リサイズしたとき、どちらが勝つのか説明できない）。
        ///
        /// ★ 高さで見る。窓の縦横比が変わっても（#88）権威は高さのままで、幅は同じ倍率で付いてくる。
        /// </summary>
        public static float ScaleForWindow(float height, float baseHeight)
        {
            if (!(baseHeight > 0f) || !(height > 0f)) return 1f;
            return Clamp(RoundToStep(height / baseHeight, ScaleStep), ScaleMin, ScaleMax);
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
        /// ★★ <b><c>&lt; 1</c> の裸の比較にしないこと。</b> 0.1 刻みに丸めた後でも
        ///   <c>1.0f</c> ちょうどになる保証は無いので、<c>0.9999999</c> に
        ///   <c>-v 0.9999999</c> が付く。刻みの半分を許容幅にする。
        ///   ★ 上限が 1.0 に下がった（→ <see cref="VolumeMax"/>）ので
        ///   「大きくする側が効かなくなる」という以前の理由は消えたが、
        ///   <b>この判定を <c>&lt; 1</c> に「単純化」しない理由は残っている</b>。
        ///
        /// ★ 等倍のときだけ引数を増やさない（＝ #76 より前の挙動をそのまま保つ）。
        /// </summary>
        public static bool NeedsVolumeArgument(float volume)
        {
            return Math.Abs(volume - 1f) > VolumeStep / 2f;
        }

        /// <summary>
        /// フレームレート上限（<c>display.frameRate</c>、→ #88）の選べる値。
        ///
        /// ★ <b>2値しか許さない。</b> 音量や速さのような連続量と違い、中間の値
        ///   （45fps）に意味が無い —— 合成する側の刻みではなく <c>Application.targetFrameRate</c>
        ///   にそのまま渡る整数。
        /// </summary>
        public static readonly int[] FrameRateChoices = { 30, 60 };

        /// <summary>
        /// フレームレート上限の既定値。
        ///
        /// ★★ <b>既定を変えるならここだけ直すこと。</b> <see cref="MascotSettings.Defaults"/> は
        ///   この定数を読むだけにしてある —— A/B で既定を測り直すとき（30 か 60 か）に
        ///   直す場所を1つに保つため。
        /// </summary>
        public const int DefaultFrameRate = 30;

        /// <summary>
        /// <see cref="FrameRateChoices"/> に無い値は既定へ倒す。
        ///
        /// ★ <b>クランプ（一番近い値へ丸める）ではないこと。</b> 音量や速さの
        ///   <see cref="Normalize"/> と違い、選べる値がちょうど2つしか無いので
        ///   「近い方」に丸める理由が無い（45 が 30 と 60 のどちらの意図か決めようが無い）。
        ///   壊れた値・古い版の値は素直に既定へ倒す。
        /// </summary>
        public static int NormalizeFrameRate(int value)
        {
            for (var i = 0; i < FrameRateChoices.Length; i++)
            {
                if (FrameRateChoices[i] == value) return value;
            }
            return DefaultFrameRate;
        }

        /// <summary>
        /// <c>display.frameRate</c> を反映していいプラットフォームか（→ <c>MascotRunner.targetFrameRate</c>
        /// の doc）。
        ///
        /// ★ <b>許可リストで書くこと</b>（<c>Vrm.UnlitFallbackPolicy.AppliesTo</c> と同じ流儀）。
        ///   否定形にすると、これから増えるプラットフォームが確かめないまま巻き込まれる。
        ///   選べる値がちょうど2つ（30 / 60）しか無く、ヘッドセットのリフレッシュレートに
        ///   合わせる話（#99）が入るまでは、それ以外のプラットフォームで上書きすると
        ///   意図しない値に黙って揃えてしまう。
        /// </summary>
        public static bool AppliesFrameRate(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return true;
                default:
                    return false;
            }
        }
    }
}
