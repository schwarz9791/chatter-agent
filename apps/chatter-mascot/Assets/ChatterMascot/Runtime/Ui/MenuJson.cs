using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.Ui
{
    /// <summary>ネイティブから返ってきたイベントの種類。</summary>
    public enum MenuEventKind
    {
        /// <summary>読めなかった / 知らない種類。<b>無視すること</b></summary>
        Unknown = 0,

        Menu,
        HotKey,

        /// <summary>
        /// ネイティブ側の診断。★ <b><c>NSLog</c> は Unity の <c>Player.log</c> に入らない</b>ので、
        /// ビルドした <c>.app</c> で起きたことを残すにはこの経路しかない。
        /// </summary>
        Log,
    }

    /// <summary>ネイティブ（<c>CM_EventCallback</c>）から1件届いたもの。</summary>
    public readonly struct MenuEvent
    {
        public MenuEvent(MenuEventKind kind, string key, int hotKeyId, string message = null)
        {
            Kind = kind;
            Key = key;
            HotKeyId = hotKeyId;
            Message = message;
        }

        public MenuEventKind Kind { get; }

        /// <summary><see cref="MenuEventKind.Menu"/> のときだけ意味がある</summary>
        public string Key { get; }

        /// <summary><see cref="MenuEventKind.HotKey"/> のときだけ意味がある</summary>
        public int HotKeyId { get; }

        /// <summary><see cref="MenuEventKind.Log"/> のときだけ意味がある</summary>
        public string Message { get; }
    }

    /// <summary>
    /// <see cref="MenuModel"/> ⇄ ネイティブとやり取りする JSON。
    ///
    /// ★ <b>ここは <c>ChatterMascot.Runtime</c> に置く。</b> <c>ChatterMascot.Desktop</c> は
    ///   Newtonsoft を参照していない（asmdef の参照は推移しない ——
    ///   <c>docs/mascot.md</c>「4回踏んだ」）し、こちらなら EditMode で固定できる。
    ///   <c>Desktop</c> 側は出来上がった文字列を渡すだけにする。
    ///
    /// ★ <b>読めないイベントで throw しないこと。</b> この経路は
    ///   <b>ネイティブのコールバックの中</b>から呼ばれる。managed 例外を
    ///   ネイティブのスタックへ抜かせない。
    /// </summary>
    public static class MenuJson
    {
        public static string Write(MenuModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));

            var root = new JObject();
            if (!string.IsNullOrEmpty(model.Tooltip)) root["tooltip"] = model.Tooltip;
            root["dimmed"] = model.Dimmed;

            // ★ 片方だけでも渡す。 ネイティブ側は在るものだけを NSImage に積む
            if (!string.IsNullOrEmpty(model.Icon1xPath) || !string.IsNullOrEmpty(model.Icon2xPath))
            {
                var icon = new JObject();
                if (!string.IsNullOrEmpty(model.Icon1xPath)) icon["1x"] = model.Icon1xPath;
                if (!string.IsNullOrEmpty(model.Icon2xPath)) icon["2x"] = model.Icon2xPath;
                root["icon"] = icon;
            }

            var items = new JArray();
            if (model.Entries != null)
            {
                for (var i = 0; i < model.Entries.Count; i++)
                {
                    var entry = model.Entries[i];
                    if (entry.IsSeparator)
                    {
                        items.Add(new JObject { ["separator"] = true });
                        continue;
                    }

                    // ★ key もラベルも無い項目は落とす。ネイティブ側も同じ判定で捨てる
                    if (string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(entry.Label)) continue;

                    items.Add(new JObject
                    {
                        ["key"] = entry.Key,
                        ["label"] = entry.Label,
                        ["checked"] = entry.Checked,
                        ["enabled"] = entry.Enabled,
                    });
                }
            }
            root["items"] = items;

            // ★ 整形しない。 人が読むファイルではなく、毎回の状態更新で作り直すもの
            return root.ToString(Formatting.None);
        }

        /// <summary>
        /// 読めたら true。読めなければ <see cref="MenuEventKind.Unknown"/> と
        /// <paramref name="error"/>（ログ用）。
        /// </summary>
        public static bool TryParseEvent(string raw, out MenuEvent value, out string error)
        {
            value = new MenuEvent(MenuEventKind.Unknown, null, 0);
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "イベントが空です";
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

            var type = root["type"];
            if (type == null || type.Type != JTokenType.String)
            {
                error = "type がありません";
                return false;
            }

            switch (type.Value<string>())
            {
                case "menu":
                {
                    var key = root["key"];
                    if (key == null || key.Type != JTokenType.String)
                    {
                        error = "menu に key がありません";
                        return false;
                    }
                    value = new MenuEvent(MenuEventKind.Menu, key.Value<string>(), 0);
                    return true;
                }

                case "hotkey":
                {
                    var id = root["id"];
                    if (id == null || id.Type != JTokenType.Integer)
                    {
                        error = "hotkey に id がありません";
                        return false;
                    }
                    value = new MenuEvent(MenuEventKind.HotKey, null, id.Value<int>());
                    return true;
                }

                case "log":
                {
                    var message = root["message"];
                    if (message == null || message.Type != JTokenType.String)
                    {
                        error = "log に message がありません";
                        return false;
                    }
                    value = new MenuEvent(MenuEventKind.Log, null, 0, message.Value<string>());
                    return true;
                }

                default:
                    // ★ 知らない種類は「壊れている」ではない。 新しいネイティブと
                    //   古い C# が組み合わさっただけのことがある
                    error = $"知らない種類です: \"{type.Value<string>()}\"";
                    return false;
            }
        }
    }
}
