using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniGLTF;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// VRM を読み込んで画面に出す。読み込み・フォールバック・当たり判定・画角合わせ。
    ///
    /// <b>表情もリップシンクもここではやらない</b>（#57 / #58）。
    ///
    /// ★ <b>ドラッグの付与はここではなく <c>Desktop/VrmDragHandleBinder</c>。</b>
    ///   <c>UniWindowController</c> はデスクトップ専用アセンブリなので、
    ///   全プラットフォーム向けのここから参照すると <b>Android ビルドが壊れる</b>。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VrmStage : MonoBehaviour
    {
        [Header("配置")]
        [Tooltip("読み込んだモデルをこの下にぶら下げる")]
        [SerializeField] private Transform modelAnchor;

        /// <summary>
        /// 読み込めなかったときに出しっぱなしにする Cube。
        ///
        /// ★ <b>名前で <c>GameObject.Find</c> しないこと。</b> このプロジェクトは
        ///   「対象を名前で決め打ちにしない」で通してある（→ <c>SceneFixups</c>）。
        ///
        /// ★ <b>無地の Cube が出ていること自体が可視のシグナル。</b> 同梱モデルまで
        ///   読めないのは異常事態なので、静かに何も出さないより良い。
        /// </summary>
        [Tooltip("読み込めなかったときに残すプレースホルダ")]
        [SerializeField] private GameObject placeholder;

        [Header("画角")]
        [Tooltip("モデルが画面に収まるようカメラを自動で下がらせる")]
        [SerializeField] private bool autoFrame = true;

        [Tooltip("自動フレーミングの余白。1.0 でぴったり")]
        [SerializeField] private float headroom = 1.1f;

        /// <summary>
        /// 読み込み中だけ上げるフレームレート。
        ///
        /// ★ <b><c>RuntimeOnlyAwaitCaller</c> の予算は 1ms/frame。</b> 30fps 上限のままだと
        ///   19MB / テクスチャ27枚のモデルで<b>壁時計で長くかかる</b>。その間メインスレッドが
        ///   詰まると <c>SpeechClient</c> が pong を返せず、<b>接続が繰り返し切れる</b>。
        /// </summary>
        [Header("読み込み")]
        [Tooltip("読み込み中だけ上げるフレームレート。0 以下なら上げない")]
        [SerializeField] private int loadFrameRate = 120;

        private readonly List<Action<GameObject>> _handlers = new List<Action<GameObject>>();
        private CancellationTokenSource _cancellation;
        private IDisposable _boost;
        private Vrm10Instance _instance;
        private RuntimeGltfInstance _gltf;
        private Bounds _bounds;
        private bool _framePending;
        private int _framedWidth;
        private int _framedHeight;

        /// <summary>読み込めたモデルのルート。まだなら <c>null</c>。</summary>
        public GameObject Model { get; private set; }

        /// <summary>
        /// 読み込み完了を購読する。
        ///
        /// ★ <b><c>event +=</c> にしないこと。</b> 購読者（<c>VrmDragHandleBinder</c>）は
        ///   <c>RuntimeInitializeOnLoadMethod</c> から来るので、読み込みとの前後が
        ///   実装の都合で入れ替わりうる。<b>もう読み終わっていたら即座に呼ぶ</b>形にすれば、
        ///   順序の保証に寄りかからずに済む。
        /// </summary>
        public void AddLoadedHandler(Action<GameObject> handler)
        {
            if (handler == null) return;
            _handlers.Add(handler);
            if (Model != null) Invoke(handler, Model);
        }

        private void Start()
        {
            // ★ 読み込みより前に見ること。読んでから気づくと
            //   「読めたのに真っ黒」と区別がつかない
            VrmMaterialCheck.WarnIfShadersStripped();

            _cancellation = new CancellationTokenSource();
            // MascotRunner の `_ = ShutdownThenQuitAsync()` と同じ形。
            // 例外は LoadAsync が内側で全部握る
            _ = LoadAsync(_cancellation.Token);
        }

        private void OnDisable()
        {
            _cancellation?.Cancel();
            // ★ finally が走らない経路（ドメインリロード / シーン破棄）の受け皿。
            //   Dispose は冪等なので二重解放にならない
            _boost?.Dispose();
            _boost = null;
        }

        private void OnDestroy()
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }

        private async Task LoadAsync(CancellationToken ct)
        {
            var startedAt = Time.realtimeSinceStartup;
            _boost = FrameRateBudget.Boost(loadFrameRate);

            try
            {
                var env = AssetEnvFactory.Current();
                var candidates = AssetPath.Enumerate(env, AssetKind.Vrm);

                var loaded = await VrmAssetLoader.LoadFirstAsync(candidates, ct);
                if (loaded.IsEmpty)
                {
                    Debug.LogError("[Mascot] VRM が1つも読めませんでした。探した順:" +
                                   VrmAssetLoader.DescribeCandidates(candidates));
                    return;
                }

                _instance = await UniVRM10.Vrm10.LoadBytesAsync(
                    loaded.Bytes,
                    // 1.0 だと分かっている。失敗メッセージも具体的になる
                    canLoadVrm0X: false,
                    // #59 の VRMA と手続き的アイドルが ControlRig を使う
                    controlRigGenerationOption: ControlRigGenerationOption.Generate,
                    showMeshes: true,
                    // ★ null のままにすること。Play 中なら RuntimeOnlyAwaitCaller に自動で倒れ、
                    //   マテリアルは RenderPipelineUtility が URP を自動判別する
                    awaitCaller: null,
                    materialGenerator: null,
                    vrmMetaInformationCallback: LogMeta,
                    ct: ct);

                if (_instance == null)
                {
                    Debug.LogError($"[Mascot] VRM を読み込めませんでした: {loaded.Candidate.Path}");
                    return;
                }

                Adopt(_instance);
                Debug.Log($"[Mascot] VRM の読み込み: {(int)((Time.realtimeSinceStartup - startedAt) * 1000)}ms");
            }
            catch (OperationCanceledException)
            {
                // 終了経路。ログを出さない
            }
            catch (Exception e)
            {
                // ★ 握ること。ここから漏らすと `_ = LoadAsync()` の未観測 Task として
                //   捨てられ、Cube が出たまま理由がどこにも残らない
                Debug.LogError("[Mascot] VRM の読み込みで例外が出ました: " + e);
            }
            finally
            {
                _boost?.Dispose();
                _boost = null;
            }
        }

        private void Adopt(Vrm10Instance instance)
        {
            if (modelAnchor != null) instance.transform.SetParent(modelAnchor, false);

            _gltf = instance.GetComponent<RuntimeGltfInstance>();
            VrmMaterialCheck.Inspect(_gltf);

            // ★ **bounds より先に回すこと。** Renderer.bounds はワールド軸に沿った箱なので、
            //   回したあとで測り直さないとカメラ距離がずれる
            FaceCamera(instance);

            _bounds = WorldBounds(_gltf);
            AttachCollider(instance.gameObject, _bounds);

            if (placeholder != null) placeholder.SetActive(false);

            Model = instance.gameObject;

            // ★ フレーミングと spring bone のリセットは**次のフレーム**でやる。
            //   SkinnedMeshRenderer.bounds はロード直後に確定していないことがあり、
            //   spring bone の Verlet はロード中の巨大な deltaTime で髪を吹き飛ばす
            _framePending = true;

            foreach (var handler in _handlers) Invoke(handler, Model);
        }

        private void LateUpdate()
        {
            if (_framePending)
            {
                _framePending = false;
                // ★ 読み込み中に積もった deltaTime で髪が吹き飛ぶのを戻す
                _instance?.Runtime?.SpringBone?.RestoreInitialTransform();
                _bounds = WorldBounds(_gltf);
                Frame();
                return;
            }

            if (!autoFrame || Model == null) return;

            // ★ Start() の1回では足りない。UniWindowController が起動直後に
            //   ウィンドウを作り直すので、その時点の Screen.* は最終値ではない。
            //   resizableWindow: 1 なので実行中にも変わる。
            //   OnRectTransformDimensionsChange は RectTransform 専用で 3D カメラには届かず、
            //   Unity にウィンドウリサイズの通知は無いので**ポーリングが唯一の手段**
            //   （int 2つの比較なのでコストは無視できる）
            if (Screen.width == _framedWidth && Screen.height == _framedHeight) return;
            if (Screen.width <= 0 || Screen.height <= 0) return;   // 最小化。戻ったら組み直す
            Frame();
        }

        private void Frame()
        {
            if (!autoFrame || Model == null) return;

            var camera = Camera.main;
            if (camera == null || camera.aspect <= 0f) return;

            // ★ camera.aspect を使うこと。Screen.width / Screen.height を自前で割ると
            //   ビューポート矩形を無視する。ただし**代入はしない**
            //   （一度代入すると ResetAspect() まで固定される）
            var distance = VrmFraming.Solve(_bounds, camera.fieldOfView, camera.aspect,
                                            headroom, out var axis);
            if (distance <= 0f) return;

            distance = Mathf.Max(distance, camera.nearClipPlane + _bounds.extents.z + 0.01f);
            camera.transform.position = VrmFraming.CameraPosition(_bounds, distance);

            _framedWidth = Screen.width;
            _framedHeight = Screen.height;

            // ★ 支配軸を必ず出す。「小さく映る」の原因が腕の張り出し（T ポーズ）なのか
            //   身長なのかは、これが無いと切り分けられない（#59 で腕が下りると反転する）
            Debug.Log($"[Mascot] フレーミング: {Screen.width}x{Screen.height} " +
                      $"aspect={camera.aspect:F3} bounds={_bounds.size} " +
                      $"distance={distance:F2} 支配軸={(axis == FramingAxis.Horizontal ? "水平" : "垂直")}");
        }

        /// <summary>
        /// モデルをカメラの方へ向ける。
        ///
        /// ★ <b>実機で背中が映った。</b> #56 の issue 本文は「glTF→Unity の Z 反転で
        ///   モデルが −Z を向くから 180°回転は不要」としていたが、そうならなかった。
        ///   仕様の読みに頼らず、<b>肩の並びから実際の向きを出して回す</b>
        ///   （→ <see cref="VrmOrientation"/>）。
        ///
        /// ★ <b>照明は回さない。</b> シーンの Directional Light は −Z 側（カメラ側）を
        ///   照らす向きに置いてあるので、モデルの正面をカメラへ向ければ顔に光が当たる。
        /// </summary>
        private static void FaceCamera(Vrm10Instance instance)
        {
            var animator = instance.GetComponent<Animator>();
            if (animator == null || animator.avatar == null) return;

            var left = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var right = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (left == null || right == null)
            {
                Debug.LogWarning("[Mascot] 上腕のボーンが無いので向きを判定できません");
                return;
            }

            var yaw = VrmOrientation.YawToFaceCamera(left.position, right.position);
            if (Mathf.Approximately(yaw, 0f)) return;

            instance.transform.Rotate(Vector3.up, yaw, Space.World);
            Debug.Log($"[Mascot] モデルの向きを {yaw:F0} 度回してカメラへ向けました");
        }

        /// <summary>
        /// ランタイムロードしたモデルには Collider が無い。
        /// <b>クリック透過のヒットテストとドラッグの両方がこれを見る</b>ので、
        /// 見た目と当たり判定がずれないよう bounds から起こす。
        /// </summary>
        private static void AttachCollider(GameObject root, Bounds bounds)
        {
            if (root.GetComponentInChildren<Collider>() != null) return;

            var collider = root.AddComponent<CapsuleCollider>();
            collider.direction = 1;   // Y 軸
            collider.center = root.transform.InverseTransformPoint(bounds.center);
            collider.height = Mathf.Max(bounds.size.y, 0.01f);
            // ★ **奥行き（Z）に合わせる。幅（X）に合わせない。**
            //   VRM 1.0 はレストポーズが T ポーズ必須なので、extents.x は
            //   **広げた腕の長さ**になる。そちらを採ると半径 0.695m の太い円柱ができ、
            //   250px のウィンドウで **202px（81%）** が「キャラの実体」として
            //   クリックを食う —— 腕の高さ以外の左右の空白まで掴んでしまい、
            //   クリック透過の意味がほとんど無くなる（実機で確認）。
            //   奥行きなら胴の太さに近く、80px に収まる。
            // ★ 引き換えに**伸ばした腕の上では掴めない**（クリックが下へ抜ける）。
            //   #59 でアイドルモーションが入って腕が下りれば差はほぼ消える。
            //   部位ごとの精緻化は #16。
            collider.radius = Mathf.Max(bounds.extents.z, 0.01f);
        }

        private static Bounds WorldBounds(RuntimeGltfInstance instance)
        {
            var bounds = new Bounds();
            var first = true;
            if (instance == null) return bounds;

            foreach (var renderer in instance.Renderers)
            {
                if (renderer == null) continue;
                // 空の Renderer を混ぜると原点まで bounds が伸びる
                if (renderer.bounds.size == Vector3.zero) continue;

                if (first)
                {
                    bounds = renderer.bounds;
                    first = false;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return bounds;
        }

        /// <summary>
        /// ★ 購読者の例外をここで止める。<c>SpeechClient.SafeInvoke</c> と同じ理由 ——
        ///   1人の失敗で残りの購読者と読み込み完了処理を道連れにしない。
        /// </summary>
        private static void Invoke(Action<GameObject> handler, GameObject model)
        {
            try
            {
                handler(model);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] VRM の読み込み通知で例外が出ました: " + e.Message);
            }
        }

        /// <summary>
        /// ★ <b>診断であると同時に、ライセンス上の最低限の対応。</b>
        ///   <c>creditNotation: required</c> なモデルを読ませたときに、
        ///   作者名がどこにも出ないのは避ける。
        /// </summary>
        private static void LogMeta(Texture2D thumbnail,
                                    UniGLTF.Extensions.VRMC_vrm.Meta meta,
                                    UniVRM10.Migration.Vrm0Meta vrm0Meta)
        {
            if (meta == null) return;
            var authors = meta.Authors != null ? string.Join(", ", meta.Authors) : "(不明)";
            Debug.Log($"[Mascot] モデル: \"{meta.Name}\" / 作者: {authors} / ライセンス: {meta.LicenseUrl}");
        }
    }
}
