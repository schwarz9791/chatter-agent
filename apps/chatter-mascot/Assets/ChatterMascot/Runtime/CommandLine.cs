using System;
using System.Collections.Generic;

namespace ChatterMascot
{
    /// <summary>
    /// 起動引数を読む。<c>-serverUrl</c> / <c>-vrm</c> / <c>-vrma</c> と、
    /// Editor 側の <c>-buildScene</c> / <c>-buildOutput</c> が同じ形を使う。
    ///
    /// ★ <b>取れない環境でも起動を止めないこと。</b> ここで throw すると
    ///   「動いて見える死体」ですらなく、接続先のログも出ないまま落ちる。
    ///
    /// ★ <b><c>.app</c> を Finder から起動すると引数は付かない。</b> 起動引数は
    ///   切り分けと検証のための口であって、常用の設定手段ではない
    ///   （常用は <c>~/.config/chatter-agent/</c> 側。→ <see cref="Vrm.AssetPath"/>）。
    /// </summary>
    public static class CommandLine
    {
        /// <summary>
        /// プロセスの起動引数。取れなければ空を返す。
        /// </summary>
        public static IReadOnlyList<string> Args()
        {
            try
            {
                return Environment.GetCommandLineArgs() ?? Array.Empty<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// <paramref name="name"/> の次に来る値を返す。無ければ <c>null</c>。
        /// </summary>
        public static string Argument(string name) => Argument(Args(), name);

        /// <summary>
        /// 引数列を渡す版。<b>純粋関数</b>なのでテストで固定できる。
        /// </summary>
        public static string Argument(IReadOnlyList<string> args, string name)
        {
            if (args == null || string.IsNullOrEmpty(name)) return null;

            // 末尾の name は「値が無い」ので拾わない
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }
            return null;
        }

        /// <summary>
        /// 真偽値のフラグ。<paramref name="name"/> が無ければ <paramref name="defaultValue"/>。
        ///
        /// ★ <b><c>-flag</c> を単独で渡した形（値なし）を <c>true</c> として扱うこと。</b>
        ///   <see cref="Argument"/> は「name の<b>次に来る値</b>」を返す作りなので、
        ///   <c>-faceLog</c> だけを渡すと <c>null</c> が返る。それを「指定されなかった」と
        ///   同じ扱いにすると、<b>いちばん自然な渡し方で黙って無反応</b>になる ——
        ///   切り分け中にこれを踏むと「ログが出ない＝コードが走っていない」と誤読しかねない。
        ///
        /// ★ <b>次のトークンが <c>-</c> で始まるときも「値なし」として扱うこと。</b>
        ///   そうしないと <c>-faceLog -vrm /path.vrm</c> が <c>-vrm</c> を値として食い、
        ///   <c>-vrm</c> の側は<b>解釈されないまま消える</b>。
        ///
        /// ★ <b>偽と読むのは <c>0</c> / <c>false</c> / <c>no</c> / <c>off</c> だけ</b>
        ///   （大文字小文字は無視）。それ以外の値は真。
        /// </summary>
        public static bool Flag(string name, bool defaultValue = false) => Flag(Args(), name, defaultValue);

        /// <summary>
        /// 引数列を渡す版。<b>純粋関数</b>なのでテストで固定できる。
        /// </summary>
        public static bool Flag(IReadOnlyList<string> args, string name, bool defaultValue = false)
        {
            if (args == null || string.IsNullOrEmpty(name)) return defaultValue;

            for (var i = 0; i < args.Count; i++)
            {
                if (args[i] != name) continue;

                var value = i + 1 < args.Count ? args[i + 1] : null;
                // 値なし（末尾、または次が別のフラグ）＝ 有効化の指定
                if (string.IsNullOrEmpty(value) || value[0] == '-') return true;

                return !(value == "0"
                         || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                         || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                         || value.Equals("off", StringComparison.OrdinalIgnoreCase));
            }

            return defaultValue;
        }
    }
}
