using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChatterMascot.Ui;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ChatterMascot.Net
{
    /// <summary>
    /// 起動時に GitHub の release を見て、今より新しい版があれば端末に知らせる。
    /// Play 以外から入れた APK は自動では更新されないため。
    /// 版の規則（<see cref="TryParseReleaseTag"/>）はビルドとここで共有する。
    /// </summary>
    public static class ReleaseCheck
    {
        private const string ReleasesUrl =
            "https://api.github.com/repos/schwarz9791/chatter-agent/releases?per_page=100";
        private const string PlayInstaller = "com.android.vending";
        private const string ApkAssetName = "ChatterMascot.apk";
        private const int TimeoutSeconds = 10;

        /// <summary>
        /// release タグ <c>mascot-v&lt;MAJOR&gt;.&lt;MINOR&gt;.&lt;PATCH&gt;</c> から版を決める。
        /// 各数は 0〜99・先頭ゼロ不可で、プレリリース接尾辞は持たない。
        /// <c>versionCode = MAJOR*10000 + MINOR*100 + PATCH</c> で、Android が要る 1 以上に
        /// ならない 0.0.0 は受けない。版の規則はここ1か所にだけ置く。
        /// </summary>
        public static bool TryParseReleaseTag(string tag, out string version, out int versionCode)
        {
            version = null;
            versionCode = 0;
            if (tag == null) return false;

            var match = Regex.Match(
                tag, @"\Amascot-v(0|[1-9][0-9]?)\.(0|[1-9][0-9]?)\.(0|[1-9][0-9]?)\z");
            if (!match.Success) return false;

            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);
            var patch = int.Parse(match.Groups[3].Value);
            versionCode = major * 10000 + minor * 100 + patch;
            if (versionCode == 0) return false;

            version = $"{major}.{minor}.{patch}";
            return true;
        }

        /// <summary>
        /// Play 以外から入れた Android だけが確かめる。Play は審査を経て反映されるので、GitHub を見ると
        /// まだ Play に無い版を案内してしまう。許可リストで書くこと。
        /// </summary>
        public static bool ShouldCheck(RuntimePlatform platform, string installerName)
        {
            switch (platform)
            {
                case RuntimePlatform.Android:
                    return installerName != PlayInstaller;
                default:
                    return false;
            }
        }

        /// <summary>
        /// release 一覧の JSON から、今の版より新しい最大の版を返す。無ければ・今の版が解釈できなければ・
        /// JSON が読めなければ <c>null</c>。draft / prerelease / <c>mascot-v</c> 以外のタグ・APK が付いていない release は数えない
        /// （案内しても入れる手が無い）。
        /// </summary>
        public static string Newer(string releasesJson, string currentVersion)
        {
            if (!TryParseReleaseTag("mascot-v" + currentVersion, out _, out var current)) return null;

            try
            {
                if (!(JToken.Parse(releasesJson) is JArray releases)) return null;

                string best = null;
                var bestCode = current;
                foreach (var release in releases)
                {
                    if (!(release is JObject o)) continue;
                    if (IsTrue(o["draft"]) || IsTrue(o["prerelease"])) continue;
                    var tag = o["tag_name"];
                    if (tag == null || tag.Type != JTokenType.String) continue;
                    if (!TryParseReleaseTag((string)tag, out var version, out var code)) continue;
                    if (!HasApk(o["assets"])) continue;
                    if (code <= bestCode) continue;
                    best = version;
                    bestCode = code;
                }
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool HasApk(JToken assets)
        {
            if (!(assets is JArray array)) return false;
            foreach (var asset in array)
            {
                var name = (asset as JObject)?["name"];
                if (name != null && name.Type == JTokenType.String && (string)name == ApkAssetName) return true;
            }
            return false;
        }

        private static bool IsTrue(JToken token) => token != null && token.Type == JTokenType.Boolean && (bool)token;

        /// <summary>
        /// 取得して、新しい版があれば端末に出す。失敗はログだけで端末には出さない。
        /// </summary>
        public static async Task CheckAsync(string currentVersion)
        {
            try
            {
                string body;
                using (var request = UnityWebRequest.Get(ReleasesUrl))
                {
                    request.timeout = TimeoutSeconds;
                    request.downloadHandler = new DownloadHandlerBuffer();
                    await AudioFetcher.SendAsync(request);
                    if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                    {
                        Debug.Log($"[Mascot] Release check failed: {request.result} {request.responseCode}");
                        return;
                    }
                    body = request.downloadHandler.text;
                }

                var newer = Newer(body, currentVersion);
                if (newer == null) return;
                Debug.Log($"[Mascot] A newer version is available: {newer}");
                DeviceToast.Show(UiText.For(Application.systemLanguage).UpdateAvailableToast(newer));
            }
            catch (Exception e)
            {
                Debug.Log("[Mascot] Release check failed: " + e.Message);
            }
        }
    }
}
