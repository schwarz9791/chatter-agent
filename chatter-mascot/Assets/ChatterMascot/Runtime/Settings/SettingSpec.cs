using System;
using System.Collections.Generic;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// 設定パネルの項目の種類。<b>ネイティブ（<c>CMSettingsPanel.m</c>）はこれだけを見て
    /// ビューを組む</b>ので、ここに無い見た目は作れない。
    ///
    /// ★ <b>ネイティブ側に設定のキーを書かないこと。</b> 項目の追加・並び替え・ラベルの変更が
    ///   C# だけの変更で済むのが、この作りを選んだ理由そのもの（#75 の <c>MenuModel</c> と同じ）。
    /// </summary>
    public enum SettingKind
    {
        /// <summary>見出し。値を持たない</summary>
        Section,
        Bool,
        Slider,
        Choice,
        Button,

        /// <summary>
        /// ショートカット。<b>実際にキーを押して記録する</b>（現在の値 + 「記録」ボタン）。
        ///
        /// ★★ <b>ネイティブが返すのは数値だけ。</b> <c>"&lt;keyCode&gt;,&lt;修飾マスク&gt;"</c> の形で
        ///   返り、<c>ctrl+opt+m</c> という語彙への変換は
        ///   <see cref="Ui.HotKeySpec.TryFromCode"/> が行う。
        ///   「ネイティブに設定の語彙を書かない」をショートカットにも通すため。
        ///
        /// ★ <b>キーの捕捉はローカルモニタ（<c>addLocalMonitorForEvents</c>）。</b>
        ///   グローバルモニタと違い<b>アクセシビリティ権限が要らない</b>
        ///   （グローバルショートカットの登録に Carbon を選んだのと同じ理由）。
        ///
        /// ★ <b>記録中はパネルがキーウィンドウである必要がある。</b> <c>LSUIElement</c> の
        ///   アプリは既定でアクティブになれないので、パネルを出すときの
        ///   <c>[NSApp activateIgnoringOtherApps:YES]</c> が効いていないと
        ///   <b>「記録」を押しても何も起きない</b>という形で出る。
        /// </summary>
        HotKey,

        /// <summary>
        /// <b>読み取り専用</b>の複数行テキスト（バージョン・ライセンス本文）。
        ///
        /// ★ 入力欄ではない。編集できる文字列は <see cref="HotKey"/> だけにしてある ——
        ///   自由入力の設定を増やすほど、検証と「不正値のときどう見せるか」が増える。
        /// </summary>
        Text,
    }

    /// <summary>
    /// <see cref="SettingKind.Slider"/> の読み値の<b>見せ方</b>。
    ///
    /// ★★ <b>値の意味は変えない。</b> スライダーが持つ値も、C# へ返る値も、
    ///   <c>settings.json</c> に載る値も<b>常に生の数</b>（音量なら 0.0〜1.0）。
    ///   ここで変えるのは<b>読み値のラベルだけ</b>。
    ///
    /// ★ <b>ネイティブに「その項目が何か」を知らせないための形。</b> 「音量なら % で出す」を
    ///   ObjC に書くと、設定のキーを持たせないという作りの前提が崩れる。
    /// </summary>
    public enum SettingDisplay
    {
        /// <summary>生の数をそのまま（<c>1.5</c>）。倍率のスライダー（大きさ・話す速さ）はこちら</summary>
        Number,

        /// <summary>100 倍して <c>%</c> を付ける（<c>0.7</c> → <c>70%</c>）</summary>
        Percent,
    }

    /// <summary><see cref="SettingKind.Choice"/> の選択肢1件</summary>
    public readonly struct SettingChoice : IEquatable<SettingChoice>
    {
        public SettingChoice(string value, string label)
        {
            Value = value;
            Label = label;
        }

        /// <summary>値。<b>文字列で持つ</b>（話者 ID の数値もここでは文字列にする）</summary>
        public string Value { get; }

        public string Label { get; }

        public bool Equals(SettingChoice other)
        {
            return string.Equals(Value, other.Value, StringComparison.Ordinal)
                && string.Equals(Label, other.Label, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is SettingChoice && Equals((SettingChoice)obj);

        public override int GetHashCode()
        {
            var hash = Value != null ? Value.GetHashCode() : 0;
            return (hash * 397) ^ (Label != null ? Label.GetHashCode() : 0);
        }

        public override string ToString() => Label + " (" + Value + ")";
    }

    /// <summary>
    /// 設定パネルの項目1件。<b>並び順を持つのは <see cref="SettingsSchema"/> の側</b>で、
    /// ここは1件の器に留める（<c>MenuEntry</c> と <c>MascotMenu</c> の関係と同じ）。
    ///
    /// ★ <b>ファクトリメソッドからしか作れないようにしてある。</b> 種類ごとに意味のある
    ///   フィールドが違うので、素のコンストラクタを公開すると「Bool なのに Min/Max が入っている」
    ///   ような組み合わせが作れてしまう。
    /// </summary>
    public sealed class SettingSpec
    {
        private static readonly IReadOnlyList<SettingChoice> NoChoices = new SettingChoice[0];

        private SettingSpec(SettingKind kind, string key, string label)
        {
            Kind = kind;
            Key = key;
            Label = label;
            Enabled = true;
            Note = "";
            Value = "";
            Choices = NoChoices;
        }

        public SettingKind Kind { get; private set; }

        /// <summary>
        /// 変更通知で返ってくるキー。<see cref="SettingKind.Section"/> だけ <c>null</c>。
        ///
        /// ★ <b>重複させないこと</b>（<c>SettingsSchemaTests</c> が落として教える）。
        ///   重複すると、どちらの項目を触っても同じ振り分け先に飛ぶ。
        /// </summary>
        public string Key { get; private set; }

        public string Label { get; private set; }

        /// <summary>
        /// 現在の値。<b>文字列で持つ</b>（ネイティブへは JSON で渡すので、型ごとに枝を増やさない）。
        /// <see cref="SettingKind.Bool"/> は <c>"true"</c> / <c>"false"</c>、
        /// <see cref="SettingKind.Slider"/> は <c>InvariantCulture</c> の数字。
        /// </summary>
        public string Value { get; private set; }

        public float Min { get; private set; }
        public float Max { get; private set; }

        /// <summary>
        /// スライダーの刻み。<b>ネイティブは <c>NSSlider</c> に渡すだけ</b>
        /// （<c>setNumberOfTickMarks:</c> / <c>setAllowsTickMarkValuesOnly:</c>）。
        ///
        /// ★ <b><c>max - min</c> を割り切ること</b>（<c>SettingsSchemaTests</c> が見ている）。
        ///   割り切れないと、最大値にハンドルが止まらない。
        /// </summary>
        public float Step { get; private set; }

        /// <summary>
        /// 読み値の見せ方（<see cref="SettingKind.Slider"/> のときだけ意味がある）。
        /// ★ 既定は <see cref="SettingDisplay.Number"/>。
        /// </summary>
        public SettingDisplay Display { get; private set; }

        public IReadOnlyList<SettingChoice> Choices { get; private set; }

        /// <summary>
        /// 操作できるか。
        ///
        /// ★★ <b>操作できない項目を「消す」のではなく、これを <c>false</c> にして出すこと。</b>
        ///   消すと「設定が無い」に見える。理由は <see cref="Note"/> に書く。
        /// </summary>
        public bool Enabled { get; private set; }

        /// <summary>無効な理由・補足。空なら出さない</summary>
        public string Note { get; private set; }

        /// <summary>
        /// 見出し。
        ///
        /// ★★ <b>節の全部にかかる説明は、1つ目の項目ではなくここに付けること。</b>
        ///   項目にぶら下げると<b>2つ目以降にはかかっていないように読める</b>
        ///   （「『記録』を押してキーを押してください」をミュートの行に付けていて、
        ///   キャラクターの表示切り替えにも同じことが言えるのにそう読めなかった）。
        /// </summary>
        public static SettingSpec Section(string label, string note = "")
        {
            var spec = new SettingSpec(SettingKind.Section, null, label);
            spec.Note = note ?? "";
            return spec;
        }

        public static SettingSpec Bool(string key, string label, bool value, bool enabled = true, string note = "")
        {
            var spec = new SettingSpec(SettingKind.Bool, key, label);
            spec.Value = value ? "true" : "false";
            spec.Enabled = enabled;
            spec.Note = note ?? "";
            return spec;
        }

        public static SettingSpec Slider(
            string key, string label, float value, float min, float max, float step,
            bool enabled = true, string note = "", SettingDisplay display = SettingDisplay.Number)
        {
            var spec = new SettingSpec(SettingKind.Slider, key, label);
            spec.Value = SettingsMapping.Format(SettingsMapping.RoundToStep(value, step));
            spec.Min = min;
            spec.Max = max;
            spec.Step = step;
            spec.Enabled = enabled;
            spec.Note = note ?? "";
            spec.Display = display;
            return spec;
        }

        public static SettingSpec Choice(
            string key, string label, string value, IReadOnlyList<SettingChoice> choices,
            bool enabled = true, string note = "")
        {
            var spec = new SettingSpec(SettingKind.Choice, key, label);
            spec.Value = value ?? "";
            spec.Choices = choices ?? NoChoices;
            spec.Enabled = enabled;
            spec.Note = note ?? "";
            return spec;
        }

        public static SettingSpec Button(string key, string label, bool enabled = true, string note = "")
        {
            var spec = new SettingSpec(SettingKind.Button, key, label);
            spec.Enabled = enabled;
            spec.Note = note ?? "";
            return spec;
        }

        public static SettingSpec HotKey(string key, string label, string value, bool enabled = true, string note = "")
        {
            var spec = new SettingSpec(SettingKind.HotKey, key, label);
            spec.Value = value ?? "";
            spec.Enabled = enabled;
            spec.Note = note ?? "";
            return spec;
        }

        /// <summary>
        /// <see cref="Note"/> だけ差し替えた複製。
        ///
        /// ★ 「さっき押した結果」（テスト要約の結果、拒否の理由）を載せるのに使う。
        ///   ★ <b><see cref="SettingsSchema"/> に持たせないこと</b> —— あちらは
        ///   「いまの状態」から並びを作る純粋関数で、時間の概念を入れるとテストで固定できなくなる。
        /// </summary>
        public static SettingSpec WithNote(SettingSpec source, string note)
        {
            if (source == null) return null;
            var spec = new SettingSpec(source.Kind, source.Key, source.Label);
            spec.Value = source.Value;
            spec.Min = source.Min;
            spec.Max = source.Max;
            spec.Step = source.Step;
            // ★ ここに写し忘れると、注記が付いた瞬間だけ % が消える
            spec.Display = source.Display;
            spec.Choices = source.Choices;
            spec.Enabled = source.Enabled;
            spec.Note = note ?? "";
            return spec;
        }

        public static SettingSpec Text(string key, string label, string value)
        {
            var spec = new SettingSpec(SettingKind.Text, key, label);
            spec.Value = value ?? "";
            spec.Enabled = false; // 読み取り専用
            return spec;
        }
    }
}
