using System.IO;
using UnityEditor;
using UnityEngine;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// <c>ChatterMascotNative.bundle</c>（#75）の <c>PluginImporter</c> を出荷値にする。
    ///
    /// ★★ <b>なぜ手作業ではなくスクリプトか。</b> <c>.bundle</c> は git に入れていない
    ///   （バイナリはレビューできない）ので、<b>新規クローンには <c>.meta</c> しか無い</b>。
    ///   <c>./scripts/build-native.sh</c> がバンドルを作った後、Unity が既定の設定で
    ///   インポートし直すことがあり、そのとき<b>「すべてのプラットフォーム」に化ける</b>。
    ///   化けたまま Android ビルドを回すと、macOS のバンドルを積もうとして失敗する。
    ///
    /// ★ <b>Inspector で直さないこと。</b> 手で直すと「誰かのマシンでだけ通る」状態になる。
    ///   直し方をコードに置いておけば <c>./scripts/run.sh</c> から誰でも再現できる
    ///   （<c>SceneFixups</c> と同じ立ち位置）。
    ///
    ///   ./scripts/run.sh ChatterMascot.EditorTools.NativePluginSettings.FixAll
    /// </summary>
    public static class NativePluginSettings
    {
        private const string BundlePath = "Assets/Plugins/macOS/ChatterMascotNative.bundle";

        public static void FixAll()
        {
            var fixedAny = Fix(BundlePath);
            Debug.Log(fixedAny ? "[Native] PluginImporter を出荷値にしました" : "[Native] 直すものはありませんでした");
        }

        /// <summary>直したら true。<b>バンドルが無くても失敗にしない</b>（作る前に走ることがある）。</summary>
        public static bool Fix(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Debug.Log($"[Native] まだありません（./scripts/build-native.sh で作れます）: {path}");
                return false;
            }

            var importer = AssetImporter.GetAtPath(path) as PluginImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[Native] PluginImporter として読めませんでした: {path}");
                return false;
            }

            // ★ 「すべてのプラットフォーム」を切ってから macOS だけ立てること。
            //   立てるだけだと Any が残り、Android ビルドに混ざる（→ #25）
            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, true);

            // ★ Editor でも有効にする。 Play Mode で常駐機能を触ることは無い
            //   （StatusItemBridge が Application.isEditor で降りる）が、
            //   無効にすると DllNotFoundException が Editor 側の診断経路に出て紛らわしい
            importer.SetCompatibleWithEditor(true);
            importer.SetEditorData("OS", "OSX");
            importer.SetEditorData("CPU", "AnyCPU");

            importer.SaveAndReimport();
            return true;
        }
    }
}
