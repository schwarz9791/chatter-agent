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
    ///
    /// ★ <b>読み取りは「値を1つずつ差し替える」形にしてある。</b> #75 の頃は
    ///   <c>ref</c> 引数を積んでいたが、#76 で項目が9つに増えたので
    ///   <see cref="MascotSettings"/> を返す形に変えた。項目を足すときに
    ///   引数リストを全経路で直す必要が無くなる。
    /// </summary>
    public static class SettingsJson
    {
        /// <summary>
        /// 書式のバージョン。★ <b>キーが増えるだけなら上げない</b>
        /// —— 上げると既存ユーザーの設定が1回リセットされる。#76 で6つ増えたが**上げていない**。
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
                    // ★ 刻みに丸めてから書くこと。スライダー由来の 0.7000000119 を
                    //   そのまま残すと、次に開いたときハンドルが刻みに乗らない位置から始まる
                    ["volume"] = Round(settings.Volume, SettingsMapping.VolumeStep),
                },
                ["ui"] = new JObject
                {
                    ["hideHotKey"] = settings.HideHotKey ?? HotKeySpec.DefaultHide,
                },
                // ★ ここに「大きさ」は入らない。ウィンドウの大きさは window.json が持つ
                //   （→ MascotSettings の型 doc）
                ["character"] = new JObject
                {
                    ["idleMotion"] = settings.IdleMotion,
                    ["cursorGaze"] = settings.CursorGaze,
                    ["blink"] = settings.Blink,
                    ["vrm"] = settings.VrmFileName ?? "",
                },
                // ★ いまはデスクトップの設定パネルからしか書かない項目（→ MascotSettings.FrameRate
                //   の doc）。settings.json 自体は XR（#25）と共有する前提なので、キーの置き場所は
                //   ここでよい —— ヘッドセットのリフレッシュレートに合わせる話が入るまでは既定のまま
                ["display"] = new JObject
                {
                    ["frameRate"] = settings.FrameRate,
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

            var result = MascotSettings.Defaults;

            foreach (var property in root)
            {
                switch (property.Key)
                {
                    case "version":
                        break;

                    case "audio":
                        result = ReadAudio(property.Value, result, warn);
                        break;

                    case "ui":
                        result = ReadUi(property.Value, result, warn);
                        break;

                    case "character":
                        result = ReadCharacter(property.Value, result, warn);
                        break;

                    case "display":
                        result = ReadDisplay(property.Value, result, warn);
                        break;

                    default:
                        // ★ 知らないキーは無視する（throw も既定へのリセットもしない）。
                        //   新しい版が書いた設定を古い版が読むことは普通に起きる
                        Warn(warn, $"知らないキー \"{property.Key}\" は無視します");
                        break;
                }
            }

            settings = result;
            return true;
        }

        private static MascotSettings ReadAudio(JToken raw, MascotSettings settings, Action<string> warn)
        {
            var audio = raw as JObject;
            if (audio == null)
            {
                Warn(warn, $"audio がオブジェクトではありません（{Describe(raw)}）。既定を使います");
                return settings;
            }

            foreach (var property in audio)
            {
                switch (property.Key)
                {
                    case "mute":
                        settings = settings.WithMuted(
                            ReadBool(property.Value, "audio.mute", settings.Muted, warn));
                        break;

                    case "muteHotKey":
                        settings = settings.WithMuteHotKey(
                            ReadHotKey(property.Value, "audio.muteHotKey", settings.MuteHotKey, warn));
                        break;

                    case "volume":
                        settings = settings.WithVolume(ReadNumber(
                            property.Value, "audio.volume", settings.Volume,
                            SettingsMapping.VolumeMin, SettingsMapping.VolumeMax, SettingsMapping.VolumeStep, warn));
                        break;

                    default:
                        Warn(warn, $"知らないキー \"audio.{property.Key}\" は無視します");
                        break;
                }
            }
            return settings;
        }

        private static MascotSettings ReadUi(JToken raw, MascotSettings settings, Action<string> warn)
        {
            var ui = raw as JObject;
            if (ui == null)
            {
                Warn(warn, $"ui がオブジェクトではありません（{Describe(raw)}）。既定を使います");
                return settings;
            }

            foreach (var property in ui)
            {
                switch (property.Key)
                {
                    case "hideHotKey":
                        settings = settings.WithHideHotKey(
                            ReadHotKey(property.Value, "ui.hideHotKey", settings.HideHotKey, warn));
                        break;

                    default:
                        Warn(warn, $"知らないキー \"ui.{property.Key}\" は無視します");
                        break;
                }
            }
            return settings;
        }

        private static MascotSettings ReadCharacter(JToken raw, MascotSettings settings, Action<string> warn)
        {
            var character = raw as JObject;
            if (character == null)
            {
                Warn(warn, $"character がオブジェクトではありません（{Describe(raw)}）。既定を使います");
                return settings;
            }

            foreach (var property in character)
            {
                switch (property.Key)
                {
                    case "idleMotion":
                        settings = settings.WithIdleMotion(
                            ReadBool(property.Value, "character.idleMotion", settings.IdleMotion, warn));
                        break;

                    case "cursorGaze":
                        settings = settings.WithCursorGaze(
                            ReadBool(property.Value, "character.cursorGaze", settings.CursorGaze, warn));
                        break;

                    case "blink":
                        settings = settings.WithBlink(
                            ReadBool(property.Value, "character.blink", settings.Blink, warn));
                        break;

                    case "vrm":
                        settings = settings.WithVrmFileName(
                            ReadFileName(property.Value, "character.vrm", settings.VrmFileName, warn));
                        break;

                    default:
                        Warn(warn, $"知らないキー \"character.{property.Key}\" は無視します");
                        break;
                }
            }
            return settings;
        }

        /// <summary>
        /// ★ デスクトップだけの項目（→ MascotSettings.FrameRate の doc）。<b>audio / ui / character
        ///   と同じ作法</b>：オブジェクトでなければ既定を使い、未知キーは警告して無視する。
        /// </summary>
        private static MascotSettings ReadDisplay(JToken raw, MascotSettings settings, Action<string> warn)
        {
            var display = raw as JObject;
            if (display == null)
            {
                Warn(warn, $"display がオブジェクトではありません（{Describe(raw)}）。既定を使います");
                return settings;
            }

            foreach (var property in display)
            {
                switch (property.Key)
                {
                    case "frameRate":
                        settings = settings.WithFrameRate(
                            ReadFrameRate(property.Value, "display.frameRate", settings.FrameRate, warn));
                        break;

                    default:
                        Warn(warn, $"知らないキー \"display.{property.Key}\" は無視します");
                        break;
                }
            }
            return settings;
        }

        private static bool ReadBool(JToken value, string key, bool fallback, Action<string> warn)
        {
            if (value.Type == JTokenType.Boolean) return value.Value<bool>();
            Warn(warn, $"{key} が真偽値ではありません（{value}）。既定を使います");
            return fallback;
        }

        /// <summary>
        /// 数値を読んで、<b>刻みに丸めて範囲へ収める</b>。
        ///
        /// ★ <b>範囲外を「不正」として既定に倒さないこと。</b> 範囲を狭めたときに、
        ///   前の版で保存された値が全部既定へ飛ぶ。クランプなら「一番近い有効な値」に落ちる。
        /// ★ ただし<b>数値ですらない</b>ときは既定に倒す（クランプする先が無い）。
        /// </summary>
        private static float ReadNumber(
            JToken value, string key, float fallback, float min, float max, float step, Action<string> warn)
        {
            if (value.Type != JTokenType.Float && value.Type != JTokenType.Integer)
            {
                Warn(warn, $"{key} が数値ではありません（{value}）。既定を使います");
                return fallback;
            }

            var raw = value.Value<float>();
            if (float.IsNaN(raw) || float.IsInfinity(raw))
            {
                Warn(warn, $"{key} が数値として扱えません（{value}）。既定を使います");
                return fallback;
            }

            var normalized = SettingsMapping.Normalize(raw, min, max, step);
            if (Math.Abs(normalized - raw) > step / 2f)
            {
                Warn(warn, $"{key} を {SettingsMapping.Format(normalized)} に丸めました（元の値: {SettingsMapping.Format(raw)}）");
            }
            return normalized;
        }

        /// <summary>
        /// フレームレートを読んで、<b>選べる値（30 / 60）でなければ既定へ倒す</b>。
        ///
        /// ★ <b>クランプしないこと</b>（→ <see cref="SettingsMapping.NormalizeFrameRate"/>）。
        ///   選べる値がちょうど2つしか無いので、範囲外の値を「近い方」に丸める <see cref="ReadNumber"/>
        ///   の作法はここでは採らない。
        /// </summary>
        private static int ReadFrameRate(JToken value, string key, int fallback, Action<string> warn)
        {
            if (value.Type != JTokenType.Integer)
            {
                Warn(warn, $"{key} が整数ではありません（{value}）。既定を使います");
                return fallback;
            }

            var raw = value.Value<int>();
            var normalized = SettingsMapping.NormalizeFrameRate(raw);
            if (normalized != raw)
            {
                Warn(warn, $"{key} は 30 か 60 です（{raw}）。既定の {SettingsMapping.DefaultFrameRate} を使います");
            }
            return normalized;
        }

        /// <summary>
        /// ★ <b>ここで妥当性まで見ること。</b> 「修飾キー無し」を保存できてしまうと、
        /// 次の起動でそのキーが<b>全アプリから奪われる</b>（→ <see cref="HotKeySpec"/>）。
        /// </summary>
        private static string ReadHotKey(JToken value, string key, string fallback, Action<string> warn)
        {
            if (value.Type != JTokenType.String)
            {
                Warn(warn, $"{key} が文字列ではありません（{value}）。既定を使います");
                return fallback;
            }

            var text = value.Value<string>();
            HotKeySpec spec;
            string reason;
            if (HotKeySpec.TryParse(text, out spec, out reason)) return text;
            Warn(warn, $"{key} を使えません（{reason}）。既定を使います");
            return fallback;
        }

        /// <summary>
        /// ★★ <b>ファイル名だけを受けること。</b> この値は
        /// <c>~/.config/chatter-agent/models/</c> に連結される。区切り文字を通すと
        /// <c>../../</c> でランタイムルートの外を読ませられる（<c>plugin/</c> の spool 命名で
        /// 一度潰した形と同じ）。ここは設定ファイルなので攻撃者を想定するというより、
        /// <b>手で書き換えた人が意図せず壊れた候補を作らないため</b>の門番。
        /// </summary>
        private static string ReadFileName(JToken value, string key, string fallback, Action<string> warn)
        {
            if (value.Type != JTokenType.String)
            {
                Warn(warn, $"{key} が文字列ではありません（{value}）。既定を使います");
                return fallback;
            }

            var text = value.Value<string>().Trim();
            if (text.Length == 0) return "";

            if (text.IndexOf('/') >= 0 || text.IndexOf('\\') >= 0 || text == "." || text == "..")
            {
                Warn(warn, $"{key} はファイル名だけを書いてください（区切り文字は使えません）。既定を使います");
                return fallback;
            }
            return text;
        }

        private static float Round(float value, float step)
        {
            return SettingsMapping.RoundToStep(value, step);
        }

        private static string Describe(JToken raw)
        {
            return raw == null ? "無し" : raw.Type.ToString();
        }

        private static void Warn(Action<string> warn, string message)
        {
            if (warn != null) warn(message);
        }
    }
}
