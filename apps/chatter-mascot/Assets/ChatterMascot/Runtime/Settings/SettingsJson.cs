using System;
using System.Globalization;
using System.IO;
using ChatterMascot.Ui;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// <see cref="MascotSettings"/> の読み書き。<b>core の <c>createConfigStore</c> と同じ作法</b>:
    /// 壊れた JSON は<b>全体を読み飛ばす</b>（呼び出し側が直前値を維持する）、
    /// 未知キーは<b>警告して無視</b>、不正値は<b>そのキーだけ既定に倒す</b>。
    ///
    /// ★ <b>throw しない。</b> 設定ファイルが1文字壊れただけでマスコットが出ないのは
    ///   割に合わない（<c>WindowStateJson</c> と同じ判断）。
    /// </summary>
    public static class SettingsJson
    {
        /// <summary>
        /// 書式のバージョン。★ <b>キーが増えるだけなら上げない</b>
        /// —— 上げると既存ユーザーの設定が1回リセットされる。
        /// </summary>
        public const int CurrentVersion = 1;

        public static string Write(MascotSettings settings)
        {
            var root = new JObject
            {
                ["version"] = CurrentVersion,
                ["audio"] = new JObject
                {
                    ["mute"] = settings.Muted,
                    ["muteHotKey"] = settings.MuteHotKey ?? HotKeySpec.Default,
                },
                ["ui"] = new JObject
                {
                    ["hideHotKey"] = settings.HideHotKey ?? HotKeySpec.DefaultHide,
                },
            };
            return root.ToString(Formatting.Indented) + "\n";
        }

        /// <summary>
        /// 読めたら true。<paramref name="error"/> が非 null なら<b>ファイル全体が読めなかった</b>
        /// （呼び出し側は直前値を維持すること）。キー単位の問題は
        /// <paramref name="warn"/> へ流し、そのキーだけ既定に倒す。
        /// </summary>
        public static bool TryParse(
            string raw, out MascotSettings settings, out string error, Action<string> warn)
        {
            settings = MascotSettings.Defaults;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "中身が空です";
                return false;
            }

            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(raw)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    root = JToken.Load(reader) as JObject;
                }
            }
            catch (Exception e)
            {
                error = "JSON が壊れています: " + e.Message;
                return false;
            }

            if (root == null)
            {
                error = "トップレベルがオブジェクトではありません";
                return false;
            }

            var version = root["version"];
            if (version != null && version.Type == JTokenType.Integer)
            {
                var value = version.Value<int>();
                if (value != CurrentVersion)
                {
                    error = $"version が {value.ToString(CultureInfo.InvariantCulture)} です" +
                            $"（対応しているのは {CurrentVersion}）";
                    return false;
                }
            }

            var muted = MascotSettings.Defaults.Muted;
            var hotKey = MascotSettings.Defaults.MuteHotKey;
            var hideHotKey = MascotSettings.Defaults.HideHotKey;

            foreach (var property in root)
            {
                switch (property.Key)
                {
                    case "version":
                        break;

                    case "audio":
                        ReadAudio(property.Value as JObject, property.Value, ref muted, ref hotKey, warn);
                        break;

                    case "ui":
                        ReadUi(property.Value as JObject, property.Value, ref hideHotKey, warn);
                        break;

                    default:
                        // ★ 知らないキーは無視する（throw も既定へのリセットもしない）。
                        //   新しい版が書いた設定を古い版が読むことは普通に起きる
                        Warn(warn, $"知らないキー \"{property.Key}\" は無視します");
                        break;
                }
            }

            settings = new MascotSettings(muted, hotKey, hideHotKey);
            return true;
        }

        private static void ReadAudio(
            JObject audio, JToken raw, ref bool muted, ref string hotKey, Action<string> warn)
        {
            if (audio == null)
            {
                Warn(warn, $"audio がオブジェクトではありません（{raw?.Type.ToString() ?? "無し"}）。既定を使います");
                return;
            }

            foreach (var property in audio)
            {
                switch (property.Key)
                {
                    case "mute":
                        if (property.Value.Type == JTokenType.Boolean) muted = property.Value.Value<bool>();
                        else Warn(warn, $"audio.mute が真偽値ではありません（{property.Value}）。既定を使います");
                        break;

                    case "muteHotKey":
                        ReadHotKey(property.Value, "audio.muteHotKey", ref hotKey, warn);
                        break;

                    default:
                        Warn(warn, $"知らないキー \"audio.{property.Key}\" は無視します");
                        break;
                }
            }
        }

        private static void ReadUi(
            JObject ui, JToken raw, ref string hideHotKey, Action<string> warn)
        {
            if (ui == null)
            {
                Warn(warn, $"ui がオブジェクトではありません（{raw?.Type.ToString() ?? "無し"}）。既定を使います");
                return;
            }

            foreach (var property in ui)
            {
                switch (property.Key)
                {
                    case "hideHotKey":
                        ReadHotKey(property.Value, "ui.hideHotKey", ref hideHotKey, warn);
                        break;

                    default:
                        Warn(warn, $"知らないキー \"ui.{property.Key}\" は無視します");
                        break;
                }
            }
        }

        /// <summary>
        /// ★ <b>ここで妥当性まで見ること。</b> 「修飾キー無し」を保存できてしまうと、
        /// 次の起動でそのキーが<b>全アプリから奪われる</b>（→ <see cref="HotKeySpec"/>）。
        /// </summary>
        private static void ReadHotKey(JToken value, string key, ref string target, Action<string> warn)
        {
            if (value.Type != JTokenType.String)
            {
                Warn(warn, $"{key} が文字列ではありません（{value}）。既定を使います");
                return;
            }

            var text = value.Value<string>();
            HotKeySpec spec;
            string reason;
            if (HotKeySpec.TryParse(text, out spec, out reason)) target = text;
            else Warn(warn, $"{key} を使えません（{reason}）。既定を使います");
        }

        private static void Warn(Action<string> warn, string message)
        {
            if (warn != null) warn(message);
        }
    }
}
