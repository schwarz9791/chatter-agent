using System.Collections.Generic;
using Kirurobo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

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

                var fixes = new List<string>();
                if (EnsureEventSystem()) fixes.Add("EventSystem");
                if (EnsureInputModule()) fixes.Add("InputSystemUIInputModule");
                if (EnsurePhysicsRaycaster()) fixes.Add("PhysicsRaycaster");
                foreach (var name in EnsureDragHandles()) fixes.Add("UniWindowMoveHandle(" + name + ")");

                if (fixes.Count > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    changed++;
                    Debug.Log($"[Fixups] {path} に足しました: {string.Join(", ", fixes)}");
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
        /// ★ <b><c>EventSystem</c> だけではポインタイベントは1つも配送されない</b>
        ///   （→ <see cref="EnsureInputModule"/>）。クリック透過が動いていたのは
        ///   <c>UniWindowController.HitTestByRaycast</c> が <c>RaycastAll</c> の後ろに
        ///   <c>Physics.Raycast</c> のフォールバックを持っているからで、
        ///   <b>EventSystem が仕事をしていた証拠ではない</b>。
        /// </summary>
        private static bool EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null) return false;

            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            return true;
        }

        /// <summary>
        /// ★ <b>入力モジュールが無いと、<c>EventSystem</c> があってもポインタイベントは
        ///   1つも配送されない。</b> <c>IDragHandler</c> は永久に呼ばれず、
        ///   <b>ドラッグでウィンドウが動かない</b>（エラーも出ない）。
        ///
        /// <c>ProjectSettings.asset</c> の <c>activeInputHandler: 1</c>（Input System のみ）なので
        /// <c>StandaloneInputModule</c> ではなくこちらを足す。
        /// </summary>
        private static bool EnsureInputModule()
        {
            var eventSystem = Object.FindFirstObjectByType<EventSystem>();
            if (eventSystem == null) return false;
            if (eventSystem.GetComponent<BaseInputModule>() != null) return false;

            eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            return true;
        }

        /// <summary>
        /// ★ <b>3D のコライダーに <c>EventSystem</c> のポインタイベントを届けるのに要る。</b>
        ///   これが無いと <c>RaycastAll</c> はレイキャスタを1つも持たず、
        ///   <c>UniWindowMoveHandle</c> の <c>IDragHandler</c> が呼ばれない。
        ///
        /// ★ ヒットテスト（クリック透過）の結果は変わらない。今までは
        ///   <c>UniWindowController</c> の <c>Physics.Raycast</c> フォールバックで当たっていたのが、
        ///   <c>RaycastAll</c> の側で当たるようになるだけ。
        /// </summary>
        private static bool EnsurePhysicsRaycaster()
        {
            var camera = Camera.main;
            if (camera == null) return false;
            if (camera.GetComponent<PhysicsRaycaster>() != null) return false;

            camera.gameObject.AddComponent<PhysicsRaycaster>();
            return true;
        }

        /// <summary>
        /// <b>掴める（＝クリック透過で「実体」とみなされる）ものは、ドラッグで動かせる。</b>
        ///
        /// ★ <b>対象を名前で決め打ちにしない。</b> 判定は「<c>Collider</c> を持っているか」——
        ///   クリック透過のヒットテストが <c>Physics.Raycast</c> で見ているのと同じ条件なので、
        ///   <b>掴める領域とドラッグできる領域が定義上ずれない</b>。
        ///   #17 で Cube が VRM に置き換わっても、クリック透過のために <c>Collider</c> を
        ///   付ける以上、そのままここに乗る。
        ///
        /// ★ 位置の永続化は入れていない（起動のたびに中央へ戻る）。マルチモニタ・解像度変更・
        ///   画面外からの復帰の扱いが要るので #16 でまとめて設計する。
        ///
        /// <c>UniWindowMoveHandle</c> は UniWindowController 同梱（MIT）。自前で書かないのは、
        /// <b>macOS の Retina 座標系の手当てが既に入っている</b>ため
        /// （<c>eventData.position</c> の系とウィンドウ座標系でスケールが一致しなくなる。
        /// このプロジェクトは <c>macRetinaSupport: 1</c>）。
        /// </summary>
        private static IEnumerable<string> EnsureDragHandles()
        {
            var added = new List<string>();
            if (Object.FindFirstObjectByType<UniWindowController>() == null) return added;

            foreach (var collider in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                var go = collider.gameObject;
                if (go.GetComponent<UniWindowMoveHandle>() != null) continue;

                go.AddComponent<UniWindowMoveHandle>();
                added.Add(go.name);
            }
            return added;
        }
    }
}
