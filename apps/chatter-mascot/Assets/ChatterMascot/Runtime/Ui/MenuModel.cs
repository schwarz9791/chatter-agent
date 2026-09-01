using System.Collections.Generic;

namespace ChatterMascot.Ui
{
    /// <summary>ステータスバーのメニュー1項目。</summary>
    public readonly struct MenuEntry
    {
        private MenuEntry(string key, string label, bool isChecked, bool enabled, bool separator)
        {
            Key = key;
            Label = label;
            Checked = isChecked;
            Enabled = enabled;
            IsSeparator = separator;
        }

        public string Key { get; }
        public string Label { get; }
        public bool Checked { get; }
        public bool Enabled { get; }
        public bool IsSeparator { get; }

        public static MenuEntry Separator()
        {
            return new MenuEntry(null, null, false, false, true);
        }

        public static MenuEntry Of(string key, string label, bool isChecked = false, bool enabled = true)
        {
            return new MenuEntry(key, label, isChecked, enabled, false);
        }
    }

    /// <summary>
    /// ステータスバーに出すもの一式。<b>ネイティブへ渡す前の形</b>。
    ///
    /// ★ <b>これが並び順の唯一の持ち主。</b> ObjC 側（<c>CMStatusItem.m</c>）には
    ///   キーもラベルも1つも書かれていない。項目を増やす・並べ替える・ラベルを変えるのが
    ///   C# だけの変更で済むことが、この作りを選んだ理由そのもの
    ///   （#76 の設定パネルが同じ形で乗る）。
    /// </summary>
    public sealed class MenuModel
    {
        public string Tooltip { get; set; }

        /// <summary>テンプレート画像（<c>@1x</c>）の絶対パス。<c>null</c> ならアイコンを変えない</summary>
        public string Icon1xPath { get; set; }

        public string Icon2xPath { get; set; }

        /// <summary>アイコンを薄く描く（ミュート中であることを目に見せる）</summary>
        public bool Dimmed { get; set; }

        public IReadOnlyList<MenuEntry> Entries { get; set; }
    }

    /// <summary>
    /// メニューの key。<b>C# とネイティブの間で唯一共有される語彙</b>。
    ///
    /// ★ ネイティブ側はこの文字列を<b>そのまま返すだけ</b>で、意味を知らない。
    /// </summary>
    public static class MenuKeys
    {
        public const string Mute = "mute";
        public const string Hide = "hide";
        public const string Settings = "settings";
        public const string About = "about";
        public const string Quit = "quit";
    }

    /// <summary>メニューを組むのに要る、その時々の状態。</summary>
    public readonly struct MenuState
    {
        public MenuState(
            bool muted, bool hidden, HotKeySpec muteHotKey, HotKeySpec hideHotKey,
            string productName, string version, int pid,
            string icon1xPath, string icon2xPath)
        {
            Muted = muted;
            Hidden = hidden;
            MuteHotKey = muteHotKey;
            HideHotKey = hideHotKey;
            ProductName = productName;
            Version = version;
            Pid = pid;
            Icon1xPath = icon1xPath;
            Icon2xPath = icon2xPath;
        }

        public bool Muted { get; }
        public bool Hidden { get; }
        public HotKeySpec MuteHotKey { get; }
        public HotKeySpec HideHotKey { get; }
        public string ProductName { get; }
        public string Version { get; }
        public int Pid { get; }
        public string Icon1xPath { get; }
        public string Icon2xPath { get; }
    }

    /// <summary>メニューの並びを決める唯一の場所。</summary>
    public static class MascotMenu
    {
        public static MenuModel Build(MenuState state)
        {
            var product = string.IsNullOrEmpty(state.ProductName) ? "Chatter Mascot" : state.ProductName;

            var entries = new List<MenuEntry>
            {
                MenuEntry.Of(
                    MenuKeys.Mute,
                    WithShortcut("ミュート", state.MuteHotKey),
                    isChecked: state.Muted),
                MenuEntry.Of(
                    MenuKeys.Hide,
                    WithShortcut(
                        state.Hidden ? "キャラクターを表示する" : "キャラクターを隠す",
                        state.HideHotKey)),
                MenuEntry.Of(MenuKeys.Settings, "設定を開く…"),
                MenuEntry.Separator(),

                // ★ #76 で押せるようになった。押すと設定パネルが開き、末尾に版とライセンスがある。
                //   版をここに出し続けるのは、Dock に居ないので「どれが動いているか」の
                //   手掛かりがここしか無いため
                MenuEntry.Of(
                    MenuKeys.About,
                    string.IsNullOrEmpty(state.Version) ? product : $"{product} {state.Version}"),
                MenuEntry.Of(MenuKeys.Quit, "終了"),
            };

            return new MenuModel
            {
                // ★ pid を入れること。 Dock に出ない以上、二重起動は
                //   「アイコンが2つ並ぶ」でしか気づけない（→ #75 の LSUIElement の代償4）
                Tooltip = $"{product} (pid {state.Pid})",
                Icon1xPath = state.Icon1xPath,
                Icon2xPath = state.Icon2xPath,
                Dimmed = state.Muted,
                Entries = entries,
            };
        }

        /// <summary>
        /// ★ <b>ショートカットはラベルに書くだけ</b>（→ <see cref="HotKeySpec.FormatSymbols"/>）。
        /// ★ <b>登録できていないときは表記を出さない</b> —— 効かないショートカットを案内しない。
        /// </summary>
        private static string WithShortcut(string label, HotKeySpec hotKey)
        {
            var symbols = hotKey.FormatSymbols();
            return string.IsNullOrEmpty(symbols) ? label : $"{label}（{symbols}）";
        }
    }
}
