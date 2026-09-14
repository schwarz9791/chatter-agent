using System;
using System.IO;
using System.Xml.Linq;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// Gradle プロジェクト生成の直後に <c>AndroidManifest.xml</c> へ
    /// <c>INTERNET</c> 権限と <c>usesCleartextTraffic</c> を足す（#97 / #98）。
    ///
    /// ★ <b>なぜ <c>Assets/Plugins/Android/AndroidManifest.xml</c> を静的に置かないか。</b>
    ///   XR 向けパッケージも、同じマニフェストへ同じフック
    ///   （<see cref="IPostGenerateGradleAndroidProject"/>）で注入してくる。静的な1枚を
    ///   置くと「最終形を決める仕組み」が静的ファイルとこのフックの2つに分かれ、
    ///   どちらが勝つか・マージされるのかが読み手に伝わらない。仕組みを1つに保つ。
    ///
    /// ★ <b>ここは <see cref="System.Xml.Linq.XDocument"/> で書き換えてよい。</b>
    ///   <c>MacPostBuild</c> の plist と違って、<c>AndroidManifest.xml</c> は
    ///   DOCTYPE を持たない素直な XML なので、<c>XDocument</c> の再シリアライズで壊れない。
    ///
    /// ★ <b>プラットフォームガードは要らない。</b> このフックは Android ビルドのときにしか
    ///   呼ばれない（Unity 側の契約）。
    ///
    /// ★ <b>失敗したらビルドを止めること。</b> 注入が抜けた APK はループバック接続の間は
    ///   気づけず、LAN 上のホストへ http で繋いだときに初めて実行時に落ちる
    ///   （→ <see cref="AndroidPlayerSettings"/> の <c>insecureHttpOption</c>）。
    /// </summary>
    public sealed class AndroidManifestPostProcessor : IPostGenerateGradleAndroidProject
    {
        private static readonly XNamespace AndroidNs = "http://schemas.android.com/apk/res/android";
        private const string InternetPermission = "android.permission.INTERNET";

        public int callbackOrder => 0;

        /// <summary><paramref name="path"/> は unityLibrary モジュールのルート。</summary>
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                throw new BuildFailedException($"[Build] AndroidManifest.xml が見つかりません: {manifestPath}");
            }

            try
            {
                var document = XDocument.Load(manifestPath);
                if (!Apply(document))
                {
                    Debug.Log("[Build] AndroidManifest.xml: 既に必要な設定を持っています");
                    return;
                }

                document.Save(manifestPath);
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new BuildFailedException($"[Build] AndroidManifest.xml を編集できませんでした: {e.Message}");
            }
        }

        /// <summary>
        /// <paramref name="document"/> へ INTERNET 権限と <c>usesCleartextTraffic</c> を足す。
        /// 変更したら true、既に両方満たしていれば false。<c>manifest</c> / <c>application</c>
        /// 要素が読めなければ <see cref="BuildFailedException"/>。
        /// テストから呼ぶために <c>public</c>。
        /// </summary>
        public static bool Apply(XDocument document)
        {
            var manifest = document?.Root;
            if (manifest == null || manifest.Name.LocalName != "manifest")
            {
                throw new BuildFailedException("[Build] AndroidManifest.xml の manifest 要素が読めません");
            }

            var application = manifest.Element("application");
            if (application == null)
            {
                throw new BuildFailedException("[Build] AndroidManifest.xml に application 要素がありません");
            }

            var addedInternet = EnsureInternetPermission(manifest);
            var addedCleartext = EnsureCleartextTraffic(application);
            if (addedInternet) Debug.Log("[Build] AndroidManifest.xml: INTERNET を追加");
            if (addedCleartext) Debug.Log("[Build] AndroidManifest.xml: usesCleartextTraffic を追加");
            return addedInternet || addedCleartext;
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

        /// <summary>書いたら true。</summary>
        private static bool EnsureCleartextTraffic(XElement application)
        {
            var attribute = application.Attribute(AndroidNs + "usesCleartextTraffic");
            if (attribute != null && attribute.Value == "true") return false;

            application.SetAttributeValue(AndroidNs + "usesCleartextTraffic", "true");
            return true;
        }
    }
}
