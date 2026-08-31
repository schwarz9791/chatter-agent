using System;
using System.Collections.Generic;
using System.Text;

namespace ChatterMascot.Ui
{
    /// <summary>
    /// グローバルショートカットの指定（<c>"opt+m"</c>）と、Carbon の
    /// <c>RegisterEventHotKey</c> が要求する <c>(keyCode, modifiers)</c> の相互変換。
    ///
    /// ★ <b>ここは純粋。</b> ネイティブを呼ばないので EditMode で固定できる
    ///   （<c>ChatterMascot.Tests</c> は <c>ChatterMascot.Runtime</c> しか参照しない）。
    ///
    /// ★ <b>Carbon の仮想キーコードは「物理キーの位置」であって刻印ではない。</b>
    ///   <c>kVK_ANSI_M</c>（0x2E）は US 配列で M がある位置を指す。JIS 配列でも同じ位置に
    ///   M が刻印されているので実用上は一致するが、<b>Dvorak などでは刻印と合わない</b>。
    ///   レイアウトを見て変換する API（<c>UCKeyTranslate</c>）はあるが、
    ///   <b>ショートカットは物理位置で覚えるものなので変換しない</b>のが macOS の作法。
    ///
    /// ★ <b>修飾キー無しは受け付けない。</b> 単独のキーを登録すると、そのキーが
    ///   <b>どのアプリでも入力できなくなる</b>。ネイティブ側（<c>CM_HotKeyRegister</c>）でも
    ///   弾いているが、ここで弾けば理由をユーザーに返せる。
    /// </summary>
    public readonly struct HotKeySpec : IEquatable<HotKeySpec>
    {
        // Carbon の修飾キーマスク（HIToolbox の Events.h）
        public const uint ModifierCommand = 0x0100;
        public const uint ModifierShift = 0x0200;
        public const uint ModifierOption = 0x0800;
        public const uint ModifierControl = 0x1000;

        /// <summary>指定が無いときの既定（→ #75）</summary>
        public const string Default = "opt+m";

        /// <summary>
        /// 名前 → Carbon の仮想キーコード。
        ///
        /// ★ <b>網羅を目指さない。</b> ショートカットに現実に使われるものだけを持つ。
        ///   足すときは <b>物理キーの位置</b>を確かめること（US 配列の刻印で決まる）。
        /// </summary>
        private static readonly Dictionary<string, uint> KeyCodes = new Dictionary<string, uint>
        {
            { "a", 0x00 }, { "s", 0x01 }, { "d", 0x02 }, { "f", 0x03 }, { "h", 0x04 },
            { "g", 0x05 }, { "z", 0x06 }, { "x", 0x07 }, { "c", 0x08 }, { "v", 0x09 },
            { "b", 0x0B }, { "q", 0x0C }, { "w", 0x0D }, { "e", 0x0E }, { "r", 0x0F },
            { "y", 0x10 }, { "t", 0x11 }, { "o", 0x1F }, { "u", 0x20 }, { "i", 0x22 },
            { "p", 0x23 }, { "l", 0x25 }, { "j", 0x26 }, { "k", 0x28 }, { "n", 0x2D },
            { "m", 0x2E },
            { "1", 0x12 }, { "2", 0x13 }, { "3", 0x14 }, { "4", 0x15 }, { "5", 0x17 },
            { "6", 0x16 }, { "7", 0x1A }, { "8", 0x1C }, { "9", 0x19 }, { "0", 0x1D },
            { "space", 0x31 }, { "return", 0x24 }, { "tab", 0x30 }, { "escape", 0x35 },
            { "f1", 0x7A }, { "f2", 0x78 }, { "f3", 0x63 }, { "f4", 0x76 },
            { "f5", 0x60 }, { "f6", 0x61 }, { "f7", 0x62 }, { "f8", 0x64 },
            { "f9", 0x65 }, { "f10", 0x6D }, { "f11", 0x67 }, { "f12", 0x6F },
        };

        /// <summary>修飾キーの別名 → マスク。<b>綴りを1つに強制しない</b>（手で書く設定なので）</summary>
        private static readonly Dictionary<string, uint> Modifiers = new Dictionary<string, uint>
        {
            { "cmd", ModifierCommand }, { "command", ModifierCommand }, { "meta", ModifierCommand },
            { "opt", ModifierOption }, { "option", ModifierOption }, { "alt", ModifierOption },
            { "ctrl", ModifierControl }, { "control", ModifierControl },
            { "shift", ModifierShift },
        };

        private HotKeySpec(string key, uint keyCode, uint modifierMask)
        {
            Key = key;
            KeyCode = keyCode;
            ModifierMask = modifierMask;
        }

        /// <summary>正規化したキー名（<c>"m"</c>）。<see cref="Format"/> が使う</summary>
        public string Key { get; }

        public uint KeyCode { get; }

        public uint ModifierMask { get; }

        /// <summary>登録できる指定か（既定コンストラクタで作った値は <c>false</c>）</summary>
        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(Key) && ModifierMask != 0; }
        }

        /// <summary>
        /// <c>"opt+m"</c> を読む。読めなければ <c>false</c> と <paramref name="error"/>。
        /// ★ <b>throw しない。</b> 設定ファイルの1行が壊れているだけで起動を止めない。
        /// </summary>
        public static bool TryParse(string text, out HotKeySpec spec, out string error)
        {
            spec = default(HotKeySpec);

            if (string.IsNullOrEmpty(text))
            {
                error = "ショートカットが空です";
                return false;
            }

            var parts = text.Split('+');
            uint mask = 0;
            string keyName = null;

            for (var i = 0; i < parts.Length; i++)
            {
                var token = parts[i].Trim().ToLowerInvariant();
                if (token.Length == 0)
                {
                    error = $"ショートカットに空の要素があります: \"{text}\"";
                    return false;
                }

                uint modifier;
                if (Modifiers.TryGetValue(token, out modifier))
                {
                    mask |= modifier;
                    continue;
                }

                // ★ キーは1つだけ。"m+n" のような指定は Carbon に無い
                if (keyName != null)
                {
                    error = $"キーを2つ指定しています: \"{text}\"";
                    return false;
                }

                if (!KeyCodes.ContainsKey(token))
                {
                    error = $"知らないキーです: \"{token}\"";
                    return false;
                }
                keyName = token;
            }

            if (keyName == null)
            {
                error = $"キーがありません: \"{text}\"";
                return false;
            }

            // ★ ここが要点（→ 型の doc）
            if (mask == 0)
            {
                error = $"修飾キーが要ります（そのキーが全アプリで入力できなくなります）: \"{text}\"";
                return false;
            }

            spec = new HotKeySpec(keyName, KeyCodes[keyName], mask);
            error = null;
            return true;
        }

        /// <summary>
        /// 正規化した表記に戻す。<b><see cref="TryParse"/> と往復する</b>。
        /// 並びは macOS の表記順（⌃⌥⇧⌘）に合わせる。
        /// </summary>
        public string Format()
        {
            if (!IsValid) return string.Empty;

            var text = new StringBuilder();
            if ((ModifierMask & ModifierControl) != 0) text.Append("ctrl+");
            if ((ModifierMask & ModifierOption) != 0) text.Append("opt+");
            if ((ModifierMask & ModifierShift) != 0) text.Append("shift+");
            if ((ModifierMask & ModifierCommand) != 0) text.Append("cmd+");
            text.Append(Key);
            return text.ToString();
        }

        /// <summary>
        /// メニューに出す表記（<c>"⌥M"</c>）。
        ///
        /// ★ <b><c>NSMenuItem.keyEquivalent</c> には渡さないこと。</b> 渡すと
        ///   アプリがアクティブなときだけ効く<b>2つ目の</b>ショートカットができてしまい、
        ///   グローバル登録（Carbon）と二重に発火する。<b>ラベルに書くだけ</b>にする。
        /// </summary>
        public string FormatSymbols()
        {
            if (!IsValid) return string.Empty;

            var text = new StringBuilder();
            if ((ModifierMask & ModifierControl) != 0) text.Append('\u2303');
            if ((ModifierMask & ModifierOption) != 0) text.Append('\u2325');
            if ((ModifierMask & ModifierShift) != 0) text.Append('\u21E7');
            if ((ModifierMask & ModifierCommand) != 0) text.Append('\u2318');
            text.Append(Key.ToUpperInvariant());
            return text.ToString();
        }

        public override string ToString()
        {
            return Format();
        }

        public bool Equals(HotKeySpec other)
        {
            return KeyCode == other.KeyCode && ModifierMask == other.ModifierMask;
        }

        public override bool Equals(object obj)
        {
            return obj is HotKeySpec && Equals((HotKeySpec)obj);
        }

        public override int GetHashCode()
        {
            return (int)(KeyCode * 397u ^ ModifierMask);
        }
    }
}
