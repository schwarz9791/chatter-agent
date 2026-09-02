using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// <see cref="SettingSpec"/> の並び ⇄ ネイティブへ渡す JSON。<c>MenuJson</c> と同じ流儀。
    ///
    /// ★ <b>ここは <c>ChatterMascot.Runtime</c> に置く。</b> <c>ChatterMascot.Desktop</c> は
    ///   Newtonsoft を参照していない（asmdef の参照は推移しない）し、こちらなら
    ///   EditMode で固定できる。<c>Desktop</c> 側は出来上がった文字列を渡すだけにする。
    ///
    /// ★ <b>変更イベントの読み取りはここに無い。</b> ネイティブからのコールバックは
    ///   1本に統一してあるので、<c>MenuJson.TryParseEvent</c>（<c>type: "setting"</c>）が受ける。
    /// </summary>
    public static class SettingsPanelJson
    {
        /// <summary>
        /// パネルの「部品そのもの」の文字。★★ <b>ここまで C# が持つこと。</b>
        /// 「ボタンの文字くらいネイティブに書いてよい」で例外を1つ作ると、必ず増える
        /// （項目のラベルとの線引きが説明できなくなる）。
        /// </summary>
        private static JObject Strings()
        {
            return new JObject
            {
                ["record"] = "記録",
                ["cancel"] = "中止",
                // ★ 記録中に出る文字。修飾キーを押すとその記号に置き換わる
                ["recording"] = "キーを押す",
                // ★ 選択肢が空のときに出す。**項目ごと消さない**ための表示
                ["empty"] = "（取得できません）",
            };
        }

        public static string Write(string title, IReadOnlyList<SettingSpec> items)
        {
            var root = new JObject();
            if (!string.IsNullOrEmpty(title)) root["title"] = title;
            root["strings"] = Strings();

            var array = new JArray();
            if (items != null)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var spec = items[i];
                    if (spec == null) continue;
                    var item = WriteItem(spec);
                    if (item != null) array.Add(item);
                }
            }
            root["items"] = array;

            // ★ 整形しない。人が読むファイルではなく、開くたびに作り直すもの
            return root.ToString(Formatting.None);
        }

        private static JObject WriteItem(SettingSpec spec)
        {
            // ★ ラベルの無い項目は落とす。ネイティブ側も同じ判定で捨てる
            if (string.IsNullOrEmpty(spec.Label)) return null;
            // ★ 見出し以外はキーが要る。無いと押しても何も返らない「動いて見える死体」になる
            if (spec.Kind != SettingKind.Section && string.IsNullOrEmpty(spec.Key)) return null;

            var item = new JObject
            {
                ["kind"] = KindOf(spec.Kind),
                ["label"] = spec.Label,
            };
            if (spec.Kind == SettingKind.Section) return item;

            item["key"] = spec.Key;
            item["enabled"] = spec.Enabled;
            if (!string.IsNullOrEmpty(spec.Note)) item["note"] = spec.Note;

            switch (spec.Kind)
            {
                case SettingKind.Slider:
                    // ★ 数値として渡す。文字列にすると、ネイティブ側でロケール依存の
                    //   パースが要る（→ SettingsMapping.Format の ★★）
                    item["value"] = SettingsMapping.Parse(spec.Value, 0f);
                    item["min"] = spec.Min;
                    item["max"] = spec.Max;
                    item["step"] = spec.Step;
                    // ★ 既定（Number）のときは出さない。古いバンドルでも壊れないし、
                    //   JSON に「何も指定していない」がそのまま残る
                    if (spec.Display == SettingDisplay.Percent) item["display"] = "percent";
                    break;

                case SettingKind.Bool:
                    item["value"] = string.Equals(spec.Value, "true", StringComparison.Ordinal);
                    break;

                case SettingKind.Choice:
                {
                    item["value"] = spec.Value;
                    var choices = new JArray();
                    for (var i = 0; i < spec.Choices.Count; i++)
                    {
                        var choice = spec.Choices[i];
                        if (string.IsNullOrEmpty(choice.Value)) continue;
                        choices.Add(new JObject
                        {
                            ["value"] = choice.Value,
                            ["label"] = string.IsNullOrEmpty(choice.Label) ? choice.Value : choice.Label,
                        });
                    }
                    item["choices"] = choices;
                    break;
                }

                case SettingKind.Button:
                    // 値を持たない
                    break;

                default:
                    item["value"] = spec.Value ?? "";
                    break;
            }
            return item;
        }

        /// <summary>
        /// ★ <b>ネイティブが見る語彙。</b> <c>enum</c> の数値を渡さないこと ——
        ///   並びを変えた瞬間に、古いバンドルが別の種類として描く。
        /// </summary>
        private static string KindOf(SettingKind kind)
        {
            switch (kind)
            {
                case SettingKind.Section: return "section";
                case SettingKind.Bool: return "bool";
                case SettingKind.Slider: return "slider";
                case SettingKind.Choice: return "choice";
                case SettingKind.Button: return "button";
                case SettingKind.HotKey: return "hotkey";
                case SettingKind.Text: return "text";
                default: return kind.ToString().ToLowerInvariant();
            }
        }

        /// <summary>
        /// 変更イベントの値を <c>bool</c> として読む。
        ///
        /// ★ ネイティブは <c>"true"</c> / <c>"false"</c> を送る契約だが、
        ///   <c>"1"</c> / <c>"0"</c> も受ける（core の <c>parseBoolean</c> と同じ寛容さ）。
        /// </summary>
        public static bool ParseBool(string value, bool fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            var text = value.Trim().ToLowerInvariant();
            if (text == "true" || text == "1" || text == "yes" || text == "on") return true;
            if (text == "false" || text == "0" || text == "no" || text == "off") return false;
            return fallback;
        }

        /// <summary>変更イベントの値を <c>int</c> として読む（話者 ID）</summary>
        public static bool TryParseInt(string value, out int result)
        {
            return int.TryParse(
                value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }
}
