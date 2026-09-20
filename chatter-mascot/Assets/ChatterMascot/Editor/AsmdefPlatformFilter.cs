using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// asmdef の <c>includePlatforms</c> / <c>excludePlatforms</c> から、指定プラットフォームで
    /// そのアセンブリがコンパイルされるかを判定する。<b>Unity の規則をそのまま模した純粋関数</b>
    /// （<see cref="AndroidSceneStripper"/> が使う）。
    ///
    /// ★ <b>Unity 自身の判定規則。</b>
    ///   <c>includePlatforms</c> が空でなければホワイトリスト（含まれるときだけ true）、
    ///   そうでなく <c>excludePlatforms</c> が空でなければブラックリスト
    ///   （含まれるときだけ false）、どちらも空（または両方欠落）なら常に true。
    ///
    /// ★ <b>読めなければ「残す」（true）に倒すこと。</b> 唯一の利用者
    ///   （<see cref="AndroidSceneStripper"/>）にとって、判定に失敗した結果は
    ///   「壊れた参照が Android ビルドに残る」と「動くはずのコンポーネントを
    ///   誤って外す」の二択になる。後者は気づきにくいぶん悪い。
    /// </summary>
    public static class AsmdefPlatformFilter
    {
        public static bool IsIncluded(string asmdefJson, string platform)
        {
            if (string.IsNullOrEmpty(asmdefJson)) return true;

            JObject root;
            try
            {
                root = JObject.Parse(asmdefJson);
            }
            catch (Exception)
            {
                return true;
            }

            var include = PlatformNames(root, "includePlatforms");
            if (include.Length > 0) return include.Contains(platform);

            var exclude = PlatformNames(root, "excludePlatforms");
            if (exclude.Length > 0) return !exclude.Contains(platform);

            return true;
        }

        private static string[] PlatformNames(JObject root, string key)
        {
            return (root[key] as JArray)?.Select(token => (string)token).ToArray() ?? Array.Empty<string>();
        }
    }
}
