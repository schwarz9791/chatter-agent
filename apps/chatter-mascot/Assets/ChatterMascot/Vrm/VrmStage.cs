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
        private CapsuleCollider _collider;
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

                // ★ **「読めた」ではなく「パースまで通った」で確定する。**
                //   -vrm に VRM 0.x / 途中で切れたファイル / 素の .glb を渡すと、
                //   バイト列は読めるのに LoadBytesAsync が throw する。そこで打ち切ると
                //   **同梱モデルへ落ちない** —— 指定を1つ間違えただけで Cube が出る。
                //   `git lfs` 無しの clone（vita.vrm が 130 バイトのポインタになる）も同じ形。
                //   VrmAssetLoader が「返らない候補」を 15 秒で見切っているのと同じ趣旨を、
                //   パースにも通す
                foreach (var candidate in candidates)
                {
                    var loaded = await VrmAssetLoader.ReadAsync(candidate, ct);
                    if (loaded.IsEmpty) continue;

                    var instance = await ParseAsync(loaded, ct);
                    if (instance == null) continue;

                    _instance = instance;
                    Adopt(_instance);
                    // ★ **採った候補を出すこと。** 先頭とは限らなくなったので、
                    //   「いまどのモデルが出ているか」がこの1行でしか分からない
                    Debug.Log($"[Mascot] VRM の読み込み: {(int)((Time.realtimeSinceStartup - startedAt) * 1000)}ms" +
                              $"（{loaded.Candidate}）");
                    return;
                }

                // ★ **候補を全部並べること。** 探索順のどこで外れたかはこれが無いと分からない
                Debug.LogError("[Mascot] 読めて VRM として解釈できた候補が1つもありませんでした。探した順:" +
                               VrmAssetLoader.DescribeCandidates(candidates));
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

        /// <summary>
        /// バイト列を VRM 1.0 として解釈する。<b>失敗しても投げず <c>null</c> を返す</b> ——
        /// 「次の候補へ進む」を呼び出し側で普通の <c>continue</c> として書けるように。
        ///
        /// ★ <b><c>OperationCanceledException</c> だけは通すこと。</b> 握ると、終了時に
        ///   残りの候補を舐め直したうえで「1つも読めませんでした」と
        ///   <b>誤った <c>LogError</c> を出す</b>。
        ///
        /// ★ <b>失敗は <c>LogWarning</c> 止まり。</b> 探索順の途中で外れるのは正常な分岐
        ///   （<c>VrmAssetLoader</c> が「無い」を <c>Log</c> にしているのと同じ）。
        ///   <c>LogError</c> は全滅したときの1本だけにする。
        ///
        /// ★ <b><c>canLoadVrm0X: false</c> のとき UniVRM は <c>null</c> ではなく throw する</b>
        ///   （<c>"Failed to load as VRM 1.0"</c>）。GLB でないバイト列はさらに手前で落ちる。
        ///   両方を握ること。
        /// </summary>
        private static async Task<Vrm10Instance> ParseAsync(LoadedBytes loaded, CancellationToken ct)
        {
            try
            {
                var instance = await UniVRM10.Vrm10.LoadBytesAsync(
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
                if (instance != null) return instance;

                Debug.LogWarning($"[Mascot] {loaded.Candidate.Path} は VRM として解釈できませんでした（null）。次の候補へ進みます");
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mascot] {loaded.Candidate.Path} は VRM として解釈できませんでした: {e.Message}。次の候補へ進みます");
                return null;
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
            _collider = AttachCollider(instance.gameObject, _bounds);

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
                // ★ **カメラだけ測り直して Collider を置き去りにしないこと。**
                //   ここで測り直す理由（SkinnedMeshRenderer.bounds がロード直後に
                //   確定していない）は Collider にもそのまま当てはまる。放置すると
                //   「見えている範囲」と「掴める範囲」がずれ、クリック透過とドラッグの
                //   両方が静かに狂う（小さく出れば掴めず、大きく出れば窓全体が
                //   クリックを食う ← acd981d が避けようとした失敗そのもの）
                FitCollider(_collider, _bounds);
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
        ///
        /// ★ <b>読み込み完了の通知より前に作ること。</b> <c>DragHandles.AttachAll</c> の判定は
        ///   「<c>Collider</c> を持っているか」なので、無い状態で購読者が走ると
        ///   <b>ドラッグが1つも付かない</b>。<see cref="AddLoadedHandler"/> は sticky だが
        ///   <b>再通知はしない</b>ので、後から付けても呼び直されない。
        ///   ＝ <b>作るのは早く、寸法を合わせるのは後</b>（<see cref="FitCollider"/>）。
        ///
        /// ★ <b>既に Collider があるなら何もしない（<c>null</c> を返す）。</b> 手で置いた
        ///   当たり判定を上書きしない代わりに、<b>その Collider は測り直しの対象にもならない</b>。
        /// </summary>
        private static CapsuleCollider AttachCollider(GameObject root, Bounds bounds)
        {
            if (root.GetComponentInChildren<Collider>() != null) return null;

            var collider = root.AddComponent<CapsuleCollider>();
            collider.direction = 1;   // Y 軸
            FitCollider(collider, bounds);
            return collider;
        }

        /// <summary>
        /// ワールド bounds に合わせて寸法を当て直す。<b>何度呼んでもよい。</b>
        ///
        /// ★ <b>奥行き（Z）に合わせる。幅（X）に合わせない。</b>
        ///   VRM 1.0 はレストポーズが T ポーズ必須なので、<c>extents.x</c> は
        ///   <b>広げた腕の長さ</b>になる。そちらを採ると半径 0.695m の太い円柱ができ、
        ///   250px のウィンドウで <b>202px（81%）</b> が「キャラの実体」としてクリックを食う ——
        ///   腕の高さ以外の左右の空白まで掴んでしまい、クリック透過の意味がほとんど無くなる
        ///   （実機で確認）。奥行きなら胴の太さに近く、80px に収まる。
        /// ★ 引き換えに<b>伸ばした腕の上では掴めない</b>（クリックが下へ抜ける）。
        ///   #59 でアイドルモーションが入って腕が下りれば差はほぼ消える。
        ///   部位ごとの精緻化は #16。
        ///
        /// ★ <b><c>center</c> はローカル、<c>bounds</c> はワールド</b>なので
        ///   <c>InverseTransformPoint</c> を通す。<b><c>height</c> / <c>radius</c> は
        ///   <c>lossyScale</c> で実行時にさらに掛けられる</b>ので、<c>ModelAnchor</c> に
        ///   等倍以外のスケールを入れると当たり判定だけ二重に拡縮される
        ///   （現状 <c>SceneFixups</c> が等倍で作るので表面化していない）。
        /// </summary>
        private static void FitCollider(CapsuleCollider collider, Bounds bounds)
        {
            if (collider == null) return;

            collider.center = collider.transform.InverseTransformPoint(bounds.center);
            collider.height = Mathf.Max(bounds.size.y, 0.01f);
            collider.radius = Mathf.Max(bounds.extents.z, 0.01f);
        }

        /// <summary>
        /// ★ <b>規則は <see cref="VrmBounds"/> にある。ここに書き足さないこと。</b>
        ///   <c>VrmProbe</c>（EditMode の実測）と食い違うと、<c>VrmFramingTests</c> の定数が
        ///   「どちらの実装の出力か」分からなくなる。
        /// </summary>
        private static Bounds WorldBounds(RuntimeGltfInstance instance) =>
            instance == null ? new Bounds() : VrmBounds.Of(instance.Renderers);

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
