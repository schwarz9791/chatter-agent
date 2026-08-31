using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.Window
{
    /// <summary>
    /// <see cref="WindowState"/> の読み書き。
    ///
    /// ★ <b><c>JsonUtility</c> を使わないこと</b>（既存方針。<c>SpeechFrame</c> と同じ）。
    /// ★ <b>読めなかったら「保存が無い」に倒す。</b> throw しない ——
    ///   設定ファイルが1文字壊れただけでマスコットが出ないのは割に合わない。
    /// </summary>
    public static class WindowStateJson
    {
        /// <summary>
        /// 書式のバージョン。<b>合わなければ「保存が無い」として扱う。</b>
        ///
        /// ★ 上げるのは<b>意味が変わったとき</b>だけ（例: 単位を変える、原点を変える）。
        ///   キーが増えるだけなら上げない —— 上げると既存ユーザーの位置が1回リセットされる。
        /// </summary>
        public const int CurrentVersion = 1;

        /// <summary>単位を JSON にも書いておく。人が開いたときに px と読み違えないため。</summary>
        private const string Unit = "points";

        public static string Write(WindowState state)
        {
            var root = new JObject
            {
                ["version"] = CurrentVersion,
                ["unit"] = Unit,
                ["rect"] = new JObject
                {
                    ["x"] = state.Rect.X,
                    ["y"] = state.Rect.Y,
                    ["width"] = state.Rect.Width,
                    ["height"] = state.Rect.Height,
                },
                ["displays"] = state.DisplaySignature ?? string.Empty,
            };
            return root.ToString(Formatting.Indented) + "\n";
        }

        /// <summary>
        /// 読めたら true。<paramref name="error"/> は読めなかった理由（ログ用。null ならエラー無し）。
        /// </summary>
        public static bool TryParse(string raw, out WindowState state, out string error)
        {
            state = WindowState.None;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "中身が空です";
                return false;
            }

            JObject root;
            try
            {
                // ★ SpeechFrame と同じ理由で DateParseHandling.None。
                //   ここに日付は無いが、読み方を2通りにしない
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

            var version = AsInt(root["version"]);
            if (version != CurrentVersion)
            {
                error = $"version が {version?.ToString(CultureInfo.InvariantCulture) ?? "不明"} です" +
                        $"（対応しているのは {CurrentVersion}）";
                return false;
            }

            var rect = root["rect"] as JObject;
            if (rect == null)
            {
                error = "rect がありません";
                return false;
            }

            var x = AsFloat(rect["x"]);
            var y = AsFloat(rect["y"]);
            var width = AsFloat(rect["width"]);
            var height = AsFloat(rect["height"]);
            if (x == null || y == null || width == null || height == null)
            {
                error = "rect の x / y / width / height が数値ではありません";
                return false;
            }

            var parsed = new PointRect(x.Value, y.Value, width.Value, height.Value);
            if (!parsed.IsValid)
            {
                error = $"rect が矩形として不正です ({parsed})";
                return false;
            }

            state = new WindowState(parsed, root["displays"]?.Type == JTokenType.String
                ? root["displays"].Value<string>()
                : string.Empty);
            return true;
        }

        private static int? AsInt(JToken token)
        {
            if (token == null) return null;
            if (token.Type != JTokenType.Integer) return null;
            return token.Value<int>();
        }

        /// <summary>
        /// ★ <b>整数も浮動小数も受ける。</b> 書くときは float だが、人が手で
        ///   <c>"x": 0</c> と書き直すことはある。
        /// </summary>
        private static float? AsFloat(JToken token)
        {
            if (token == null) return null;
            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return null;
            return token.Value<float>();
        }
    }
}
