using System.Collections.Generic;
using ChatterMascot;
using ChatterMascot.Desktop;
using ChatterMascot.Vrm;
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

        private const string AnchorName = "ModelAnchor";
        private const string PlaceholderName = "ModelPlaceholder";
        private const string GazeTargetName = "GazeTarget";

        private const string GraphicsSettingsPath = "ProjectSettings/GraphicsSettings.asset";

        /// <summary>アウトラインの Renderer Feature。★ 型名で見る（型そのものは参照しない）。</summary>
        private const string OutlineFeatureType = "MToonOutlineRenderFeature";

        /// <summary>★ <b>Mobile も見ること。</b> #25 で同じ罠を再度踏まないため。</summary>
        private static readonly string[] RendererAssets =
        {
            "Assets/Settings/PC_Renderer.asset",
            "Assets/Settings/Mobile_Renderer.asset",
        };

        /// <summary>
        /// ランタイムロードで <c>Shader.Find</c> が引くシェーダー。
        /// UniVRM 自身のプロジェクトが Always Included に登録しているのと同じ2本。
        /// </summary>
        private static readonly string[] RequiredShaders =
        {
            VrmMaterialCheck.MToonUrpShaderName,
            VrmMaterialCheck.UniUnlitShaderName,
        };

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
                // ★ VrmStage より先に足すと ModelPlaceholder が Collider を持ったまま
                //   ModelAnchor の外に残る。順序を入れ替えないこと
                if (path == ProductionScene) fixes.AddRange(EnsureVrmStage());
                // ★ EnsureVrmStage の後であることが必要。ModelAnchor が無い状態で先に走ると
                //   VrmCharacter の置き場所が無い
                if (path == ProductionScene) fixes.AddRange(EnsureVrmCharacter());
                foreach (var name in EnsureDragHandles(scene)) fixes.Add("UniWindowMoveHandle(" + name + ")");

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
            if (EnsureAlwaysIncludedShaders()) changed++;

            // ★ 検査系は直さない。人が読む前提で LogError するだけにして exit コードも変えない
            AssertForwardRendering();
            AssertRendererFeatures();

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
        /// シーンに置かれている <c>Collider</c> 持ちにドラッグハンドルを付ける。
        ///
        /// ★ <b>規則そのものは <see cref="DragHandles"/> にある。</b> VRM は実行時に生えるので
        ///   ランタイム側（<c>VrmDragHandleBinder</c>）からも同じ規則を使う。
        ///   ここで判定を書き直すと、シーンに焼いた Cube と読み込んだ VRM で
        ///   <b>掴める条件が静かに食い違う</b>。
        /// </summary>
        private static IEnumerable<string> EnsureDragHandles(UnityEngine.SceneManagement.Scene scene)
        {
            var added = new List<string>();
            foreach (var root in scene.GetRootGameObjects())
            {
                added.AddRange(DragHandles.AttachAll(root));
            }
            return added;
        }

        /// <summary>
        /// <c>ModelAnchor</c> と <see cref="VrmStage"/> がシーンに居ることを保証する。
        ///
        /// <c>ModelPlaceholder</c>（Cube）は<b>消さずに <c>ModelAnchor</c> の子へ移す</b>。
        /// 読み込みに成功したら <see cref="VrmStage"/> が <c>SetActive(false)</c> する。
        /// ★ <b>無地の Cube が出ていること自体が可視のシグナル</b>なので、
        ///   同梱モデルまで読めない異常事態を静かにしない。
        /// </summary>
        private static IEnumerable<string> EnsureVrmStage()
        {
            var fixes = new List<string>();

            var placeholder = FindRoot(PlaceholderName)
                              ?? FindChildOfAnchor(PlaceholderName);

            var anchor = FindRoot(AnchorName);
            if (anchor == null)
            {
                anchor = new GameObject(AnchorName);
                fixes.Add(AnchorName);
            }

            if (placeholder != null && placeholder.transform.parent != anchor.transform)
            {
                // worldPositionStays: false —— アンカーは原点に置くので見た目は変わらないが、
                // アンカーを動かしたときに Cube が付いてこないと意味が無い
                placeholder.transform.SetParent(anchor.transform, false);
                fixes.Add(AnchorName + "/" + PlaceholderName);
            }

            var stage = Object.FindFirstObjectByType<VrmStage>(FindObjectsInactive.Include);
            if (stage == null)
            {
                stage = anchor.AddComponent<VrmStage>();
                fixes.Add(nameof(VrmStage));
            }

            // ★ [SerializeField] は Inspector からしか繋がらないので、ここで繋ぐ。
            //   名前で GameObject.Find する実装にしないための代償
            var serialized = new SerializedObject(stage);
            if (Assign(serialized, "modelAnchor", anchor.transform)) fixes.Add("VrmStage.modelAnchor");
            if (placeholder != null && Assign(serialized, "placeholder", placeholder))
            {
                fixes.Add("VrmStage.placeholder");
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();

            return fixes;
        }

        /// <summary>
        /// #59: <c>ModelAnchor</c> に <see cref="VrmCharacter"/> を足し、<c>Camera.main</c> の子に
        /// <c>GazeTarget</c> を作って結線する。
        ///
        /// ★ <b><see cref="EnsureVrmStage"/> より後に呼ぶこと。</b> <c>ModelAnchor</c> がまだ
        ///   無い状態でここが先に走ると <see cref="VrmCharacter"/> の置き場所が無い。
        /// </summary>
        private static IEnumerable<string> EnsureVrmCharacter()
        {
            var fixes = new List<string>();

            var anchor = FindRoot(AnchorName);
            if (anchor == null)
            {
                // ★ EnsureVrmStage が必ず先に作るはずだが、呼び出し順が入れ替わったときに
                //   静かに何もしないより、原因が分かるログを残す
                Debug.LogWarning($"[Fixups] {AnchorName} が無いので {nameof(VrmCharacter)} を足せません" +
                                  $"（{nameof(EnsureVrmStage)} より後に呼びましたか？）");
                return fixes;
            }

            var character = anchor.GetComponent<VrmCharacter>();
            if (character == null)
            {
                character = anchor.AddComponent<VrmCharacter>();
                fixes.Add(nameof(VrmCharacter));
            }

            var camera = Camera.main;
            Transform gazeTarget = null;
            if (camera != null)
            {
                var existing = camera.transform.Find(GazeTargetName);
                if (existing == null)
                {
                    var go = new GameObject(GazeTargetName);
                    go.transform.SetParent(camera.transform, false);
                    go.transform.localPosition = Vector3.zero;
                    gazeTarget = go.transform;
                    fixes.Add("Main Camera/" + GazeTargetName);
                }
                else
                {
                    gazeTarget = existing;
                }
            }
            else
            {
                Debug.LogWarning("[Fixups] Camera.main が無いので GazeTarget を作れません");
            }

            var stage = Object.FindFirstObjectByType<VrmStage>(FindObjectsInactive.Include);
            if (stage == null)
            {
                Debug.LogWarning($"[Fixups] {nameof(VrmStage)} が見つからないので {nameof(VrmCharacter)}.stage を結線できません");
            }

            var runner = Object.FindFirstObjectByType<MascotRunner>(FindObjectsInactive.Include);
            if (runner == null)
            {
                // ★ VrmCharacter.Start の FindFirstObjectByType<MascotRunner>() フォールバックが
                //   効くので致命ではないが、両方無いと SpeakingView は常に false を返し、
                //   kind: "prompt" の区別と発話中のゲインが丸ごと無効になる
                Debug.LogWarning($"[Fixups] {nameof(MascotRunner)} が見つからないので {nameof(VrmCharacter)}.runner を結線できません" +
                                  $"（{nameof(VrmCharacter)}.Start のフォールバックはあるが、両方無いと SpeakingView は常に false）");
            }

            // ★ [SerializeField] は Inspector からしか繋がらないので、ここで繋ぐ
            var serialized = new SerializedObject(character);
            if (stage != null && Assign(serialized, "stage", stage)) fixes.Add("VrmCharacter.stage");
            if (runner != null && Assign(serialized, "runner", runner)) fixes.Add("VrmCharacter.runner");
            if (gazeTarget != null && Assign(serialized, "gazeTarget", gazeTarget)) fixes.Add("VrmCharacter.gazeTarget");
            serialized.ApplyModifiedPropertiesWithoutUndo();

            return fixes;
        }

        private static bool Assign(SerializedObject serialized, string field, Object value)
        {
            var property = serialized.FindProperty(field);
            if (property == null) return false;
            if (property.objectReferenceValue == value) return false;

            property.objectReferenceValue = value;
            return true;
        }

        private static GameObject FindRoot(string name)
        {
            foreach (var root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == name) return root;
            }
            return null;
        }

        private static GameObject FindChildOfAnchor(string name)
        {
            var anchor = FindRoot(AnchorName);
            if (anchor == null) return null;

            var child = anchor.transform.Find(name);
            return child != null ? child.gameObject : null;
        }

        /// <summary>
        /// ★ <b>ランタイムロードでは、シーンのマテリアルから参照されないシェーダーが
        ///   ビルドから落ちる。</b> <c>UrpVrm10MToon10MaterialImporter</c> は
        ///   <c>Shader.Find</c> で引くので、落ちていると<b>モデルは読めるのに真っ黒／ピンク</b>に
        ///   なり、<b>例外は1つも出ない</b>。UniVRM 側に自動対策は無い。
        ///
        /// ★ <b><c>Universal Render Pipeline/Lit</c> は絶対に入れないこと。</b>
        ///   UniVRM 公式が「ビルド時間が過大になる」と明記している。
        ///   同梱モデルは 15 マテリアル全部が MToon なので不要。
        /// </summary>
        private static bool EnsureAlwaysIncludedShaders()
        {
            var wanted = new List<Shader>();
            foreach (var name in RequiredShaders)
            {
                var shader = Shader.Find(name);
                if (shader == null)
                {
                    Debug.LogError($"[Fixups] シェーダーが見つかりません: {name}" +
                                   "（UniVRM が入っていない可能性があります）");
                    continue;
                }
                wanted.Add(shader);
            }
            if (wanted.Count == 0) return false;

            try
            {
                var assets = AssetDatabase.LoadAllAssetsAtPath(GraphicsSettingsPath);
                if (assets == null || assets.Length == 0 || assets[0] == null)
                {
                    Debug.LogError($"[Fixups] {GraphicsSettingsPath} を読めませんでした");
                    return false;
                }

                var serialized = new SerializedObject(assets[0]);
                var list = serialized.FindProperty("m_AlwaysIncludedShaders");
                if (list == null)
                {
                    Debug.LogError("[Fixups] m_AlwaysIncludedShaders が見つかりません");
                    return false;
                }

                var present = new HashSet<Object>();
                for (var i = 0; i < list.arraySize; i++)
                {
                    present.Add(list.GetArrayElementAtIndex(i).objectReferenceValue);
                }

                var added = new List<string>();
                foreach (var shader in wanted)
                {
                    if (present.Contains(shader)) continue;

                    list.InsertArrayElementAtIndex(list.arraySize);
                    list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                    added.Add(shader.name);
                }

                // 既に目的の状態なら書かない（AssetDatabase.SaveAssets の再シリアライズを減らす）
                if (added.Count == 0) return false;

                serialized.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log($"[Fixups] Always Included Shaders に足しました: {string.Join(", ", added)}");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[Fixups] GraphicsSettings を操作できませんでした: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// ★ <b>MToon10(URP) に <c>UniversalGBuffer</c> パスは無い。</b>
        ///   Deferred のままだと未検証の経路に入る。UniVRM 公式の URP サンプルも Forward。
        ///
        /// <b>直さず報告するだけ</b>にしてあるのは、<c>.asset</c> の描画設定を
        /// コードで書き換えると差分の意図が読めなくなるため。
        /// </summary>
        private static void AssertForwardRendering()
        {
            foreach (var path in RendererAssets)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null) continue;

                var mode = new SerializedObject(asset).FindProperty("m_RenderingMode");
                if (mode == null || mode.intValue == 0) continue;

                Debug.LogError($"[Fixups] {path} が Forward ではありません（m_RenderingMode: {mode.intValue}）。" +
                               "MToon10(URP) に UniversalGBuffer パスは無いので Forward にしてください");
            }
        }

        /// <summary>
        /// ★ <b><c>MToonOutlineRenderFeature</c> が無いとアウトラインだけ出ない。</b>
        ///   <c>MToonOutline</c> パスは Renderer Feature が <c>EnqueuePass</c> しない限り
        ///   描画されず、<b>エラーも出ない</b>。
        ///
        /// ★ <b>追加は Unity Editor の GUI で行うこと。</b> <c>m_RendererFeatureMap</c> の
        ///   ハッシュをコードで組むのは脆い。ここは<b>検査だけ</b>。
        /// </summary>
        private static void AssertRendererFeatures()
        {
            foreach (var path in RendererAssets)
            {
                var found = false;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset != null && asset.GetType().Name == OutlineFeatureType) found = true;
                }
                if (found) continue;

                Debug.LogError($"[Fixups] {path} に {OutlineFeatureType} がありません。" +
                               "アウトラインが出ません（Inspector の Add Renderer Feature から足してください）");
            }
        }
    }
}
