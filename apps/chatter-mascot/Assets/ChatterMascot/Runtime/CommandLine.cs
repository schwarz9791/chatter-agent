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
    }
}
