using System;
using System.Collections.Generic;
using System.IO;
using ChatterMascot.Vrm;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.Settings
{
    public enum MascotRequestType
    {
        ResetWindow,
        PlayMotion,
    }

    /// <summary>
    /// ChatterAgent からマスコットへの依頼（<c>mascot/requests/*.json</c> の中身）と、
    /// 一覧（<c>mascot/motions.json</c>）の本文。<b>純粋</b>。
    ///
    /// ★ <b>壊れた・版違い・未知の依頼は throw せず、理由つきで弾く。</b> 呼び手は警告して消す。
    /// </summary>
    public readonly struct MascotRequest
    {
        public const int CurrentVersion = 1;

        public MascotRequestType Type { get; }

        /// <summary><see cref="MascotRequestType.PlayMotion"/> のときの <c>"&lt;カテゴリ&gt;/&lt;ファイル名&gt;"</c>。</summary>
        public string Id { get; }

        private MascotRequest(MascotRequestType type, string id)
        {
            Type = type;
            Id = id;
        }

        public static bool TryParse(string text, out MascotRequest request, out string reason)
        {
            request = default;
            reason = null;

            JObject root;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text ?? "")))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    root = JToken.Load(reader) as JObject;
                }
            }
            catch (Exception e)
            {
                reason = "JSON が壊れています: " + e.Message;
                return false;
            }

            if (root == null)
            {
                reason = "トップレベルがオブジェクトではありません";
                return false;
            }

            var version = root["version"];
            if (version == null || version.Type != JTokenType.Integer || (long)version != CurrentVersion)
            {
                reason = $"version が {CurrentVersion} ではありません";
                return false;
            }

            var type = root["type"];
            var typeName = type != null && type.Type == JTokenType.String ? (string)type : null;
            switch (typeName)
            {
                case "resetWindow":
                    request = new MascotRequest(MascotRequestType.ResetWindow, null);
                    return true;

                case "playMotion":
                    var id = root["id"];
                    var idText = id != null && id.Type == JTokenType.String ? (string)id : null;
                    if (string.IsNullOrEmpty(idText))
                    {
                        reason = "playMotion に id がありません";
                        return false;
                    }
                    request = new MascotRequest(MascotRequestType.PlayMotion, idText);
                    return true;

                default:
                    reason = $"知らない type です（{typeName ?? "なし"}）";
                    return false;
            }
        }

        /// <summary>
        /// ファイル名の一覧から、拡張子が<b>完全に <c>.json</c></b> のものだけを名前順に並べる。
        ///
        /// ★ 書き手は <c>*.json.tmp</c> に書いてから rename する。書きかけを拾わないこと。
        /// ★ 名前は <c>{ミリ秒}-{連番}.json</c> で、辞書順がそのまま依頼の順になる。
        /// </summary>
        public static List<string> OrderedRequestNames(IEnumerable<string> names)
        {
            var result = new List<string>();
            foreach (var name in names)
            {
                if (string.Equals(Path.GetExtension(name), ".json", StringComparison.Ordinal)) result.Add(name);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary><c>motions.json</c> の本文。id は <see cref="SettingsSchema.MotionPreviewId"/> のまま並べる。</summary>
        public static string MotionsJson(IReadOnlyList<MotionClip> clips)
        {
            var ids = new JArray();
            if (clips != null)
            {
                foreach (var clip in clips) ids.Add(SettingsSchema.MotionPreviewId(clip));
            }

            var root = new JObject { ["version"] = CurrentVersion, ["motions"] = ids };
            return root.ToString(Formatting.Indented) + "\n";
        }
    }
}
