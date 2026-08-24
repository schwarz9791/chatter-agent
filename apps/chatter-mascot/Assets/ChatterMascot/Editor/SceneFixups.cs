using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// プロジェクトとシーンに「無いと静かに壊れるもの」が揃っているか保証する。
    /// <c>-executeMethod ChatterMascot.EditorTools.SceneFixups.FixAll</c> で回す。
    ///
    /// ★ <b>シーンを作り直したら必ず走らせること。</b> 足りないと出るのは
    ///   例外ログだけで、見た目には「クリック透過が効かない」としか分からない。
    /// </summary>
    public static class SceneFixups
    {
        private static readonly string[] Scenes =
        {
            "Assets/Scenes/Mascot.unity",
            "Assets/Scenes/TransparencyProbe.unity",
        };

        /// <summary>
        /// ビルド対象。<c>File &gt; Build Settings</c> や <c>-buildScene</c> を渡さない経路が使う。
        /// </summary>
        private const string ProductionScene = "Assets/Scenes/Mascot.unity";

        public static void FixAll()
        {
            var changed = 0;
            foreach (var path in Scenes)
            {
                if (!System.IO.File.Exists(path))
                {
                    Debug.Log($"[Fixups] {path} は無いので飛ばします");
                    continue;
                }

                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var touched = EnsureEventSystem();
                if (touched)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    changed++;
                    Debug.Log($"[Fixups] {path} に EventSystem を足しました");
                }
                else
                {
                    Debug.Log($"[Fixups] {path} は問題なし");
                }
            }

            if (EnsureBuildScenes()) changed++;

            Debug.Log($"[Fixups] 完了（{changed} 件を更新）");
            EditorApplication.Exit(0);
        }

        /// <summary>
        /// ★ <b>ビルド対象シーンを本番シーン1本に固定する。</b>
        ///
        /// <c>scripts/build.sh</c> は <c>-buildScene</c> を明示で渡すので通るが、
        /// <b>Unity の <c>File &gt; Build Settings &gt; Build</c> や、
        /// <c>-buildScene</c> を渡さない経路（#54 の CI）は EditorBuildSettings を見る</b>。
        /// テンプレート既定の <c>SampleScene</c> のままだと、そこには
        /// <c>MascotRunner</c> も <c>UniWindowController</c> も <c>EventSystem</c> も無いので、
        /// 出来上がる <c>.app</c> は<b>不透明なウィンドウが出て、何にも繋がらず、エラーも出さない</b>。
        /// </summary>
        private static bool EnsureBuildScenes()
        {
            var current = EditorBuildSettings.scenes;
            if (current.Length == 1 && current[0].enabled && current[0].path == ProductionScene) return false;

            var scenes = new List<EditorBuildSettingsScene>
            {
                new EditorBuildSettingsScene(ProductionScene, true),
            };
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log($"[Fixups] ビルド対象シーンを {ProductionScene} にしました");
            return true;
        }

        /// <summary>
        /// ★ <b>UniWindowController の Raycast ヒットテストは <c>EventSystem.current</c> を要求する。</b>
        ///
        /// 無いと <c>HitTestByRaycast</c> が<b>毎フレーム <c>NullReferenceException</c></b> を投げ、
        /// クリック透過が一切効かなくなる。ウィンドウの透過そのものは成立するので、
        /// <b>ビルドしたアプリのログを読むまで気づけない</b>。
        ///
        /// 入力モジュール（<c>InputSystemUIInputModule</c> など）は足していない。
        /// UniWindowController が使うのは <c>RaycastAll</c> だけで、
        /// UI の操作が要るのは #16 から。
        /// </summary>
        private static bool EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null) return false;

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            return true;
        }
    }
}
