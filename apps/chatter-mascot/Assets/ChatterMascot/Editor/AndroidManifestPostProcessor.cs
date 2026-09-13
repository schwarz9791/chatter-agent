using System;
using System.IO;
using System.Xml.Linq;
using UnityEditor.Android;
using UnityEngine;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// Gradle プロジェクト生成の直後に <c>AndroidManifest.xml</c> へ
    /// <c>INTERNET</c> 権限と <c>usesCleartextTraffic</c> を足す（#97 / #98）。
    ///
    /// ★ <b>なぜ <c>Assets/Plugins/Android/AndroidManifest.xml</c> を静的に置かないか。</b>
    ///   #99 で入る XR 向けパッケージも、同じマニフェストへ同じフック
    ///   （<see cref="IPostGenerateGradleAndroidProject"/>）で注入してくる。静的な1枚を
    ///   置くと「最終形を決める仕組み」が静的ファイルとこのフックの2つに分かれ、
    ///   どちらが勝つか・マージされるのかが読み手に伝わらない。仕組みを1つに保つ。
    ///
    /// ★ <b>ここは <see cref="System.Xml.Linq.XDocument"/> で書き換えてよい。</b>
    ///   <c>MacPostBuild</c> の plist と違って、<c>AndroidManifest.xml</c> は
    ///   DOCTYPE を持たない素直な XML なので、<c>XDocument</c> の再シリアライズで
    ///   壊れる（plist 側で実測した2つの不具合）が起きない。
    ///
    /// ★ <b>プラットフォームガードは要らない。</b> このフックは Android ビルドのときにしか
    ///   呼ばれない（Unity 側の契約）。
    ///
    /// ★ <b>失敗してもビルドを落とさないこと。</b> ここで転んで得られる損失は
    ///   「ネット権限が無い／平文通信ができない」で気づきにくいが、例外を投げて
    ///   ビルドそのものを失敗させる方が実害が大きい。
    /// </summary>
    public sealed class AndroidManifestPostProcessor : IPostGenerateGradleAndroidProject
    {
        private static readonly XNamespace AndroidNs = "http://schemas.android.com/apk/res/android";
        private const string InternetPermission = "android.permission.INTERNET";

        public int callbackOrder => 0;

        /// <summary><paramref name="path"/> は unityLibrary モジュールのルート。</summary>
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            try
            {
                var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
                if (!File.Exists(manifestPath))
                {
                    Debug.LogWarning($"[Build] AndroidManifest.xml が見つかりません: {manifestPath}");
                    return;
                }

                var document = XDocument.Load(manifestPath);
                var manifest = document.Root;
                if (manifest == null)
                {
                    Debug.LogWarning("[Build] AndroidManifest.xml の manifest 要素が読めませんでした");
                    return;
                }

                var addedInternet = EnsureInternetPermission(manifest);
                var addedCleartext = EnsureCleartextTraffic(manifest);

                if (!addedInternet && !addedCleartext)
                {
                    Debug.Log("[Build] AndroidManifest.xml: 既に必要な設定を持っています");
                    return;
                }

                document.Save(manifestPath);
                if (addedInternet) Debug.Log("[Build] AndroidManifest.xml: INTERNET を追加");
                if (addedCleartext) Debug.Log("[Build] AndroidManifest.xml: usesCleartextTraffic を追加");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Build] AndroidManifest.xml を編集できませんでした: " + e.Message);
            }
        }

        /// <summary>足したら true。</summary>
        private static bool EnsureInternetPermission(XElement manifest)
        {
            foreach (var element in manifest.Elements("uses-permission"))
            {
                var name = element.Attribute(AndroidNs + "name");
                if (name != null && name.Value == InternetPermission) return false;
            }

            manifest.Add(new XElement("uses-permission", new XAttribute(AndroidNs + "name", InternetPermission)));
            return true;
        }

        /// <summary>
        /// 書いたら true。<c>application</c> 要素が無ければ何もしない
        /// （テンプレートが破損している異常系。try/catch の外側で警告済みにはしない）。
        /// </summary>
        private static bool EnsureCleartextTraffic(XElement manifest)
        {
            var application = manifest.Element("application");
            if (application == null)
            {
                Debug.LogWarning("[Build] AndroidManifest.xml に application 要素がありません");
                return false;
            }

            var attribute = application.Attribute(AndroidNs + "usesCleartextTraffic");
            if (attribute != null && attribute.Value == "true") return false;

            application.SetAttributeValue(AndroidNs + "usesCleartextTraffic", "true");
            return true;
        }
    }
}
