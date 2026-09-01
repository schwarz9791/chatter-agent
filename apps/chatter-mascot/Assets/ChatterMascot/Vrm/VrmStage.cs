using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UniGLTF;
using UniGLTF.Utils;
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
        /// ボーンから測った bounds を膨らませる余白（メートル）。
        ///
        /// ★ <b>髪・裾・頭頂・靴底はボーンに含まれない。</b> <see cref="MeasureBounds"/> は
        ///   Humanoid ボーンのワールド位置だけを見るので、これらの部位のぶん
        ///   実際の見た目より箱がわずかに小さくなる。両側に足して吸収する。
        /// </summary>
        [Tooltip("髪・裾・頭頂・靴底のぶん。ボーンより外側にある部分を吸収する余白（メートル）")]
        [SerializeField] private float boneBoundsMarginMeters = DefaultBoneBoundsMarginMeters;

        /// <summary>
        /// <see cref="boneBoundsMarginMeters"/> の既定値。
        ///
        /// ★ <b>定数として公開するのは <c>VrmProbe</c>（Editor）のため。</b> あちらはシーンを
        ///   経由せず VRM を直接読むので、<c>[SerializeField]</c> の値を取れない。定数を共有すれば
        ///   probe の出力と実行時の箱が既定構成で一致し、その出力を貼った
        ///   <c>Tests/Editor/VrmFramingTests.cs</c> の定数も実行時の値と一致する。
        /// ★ <b>シーンで <see cref="boneBoundsMarginMeters"/> を既定から変えたら、probe の出力は
        ///   実行時の箱と食い違う。</b> テストの定数を貼り直すときは両方を見ること。
        /// </summary>
        public const float DefaultBoneBoundsMarginMeters = 0.1f;

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

        /// <summary>
        /// この時刻（<c>Time.realtimeSinceStartup</c>）までは毎秒ボーンを測り直す。
        ///
        /// ★ <b>VRMA（アイドルモーション）の読み込みがいつ終わるかは <c>VrmStage</c> から
        ///   分からない。</b> <c>VrmCharacter</c> が <c>VrmStage.AddLoadedHandler</c> の
        ///   通知を受けてから非同期に読み込むので、<c>_framePending</c> の1回だけでは
        ///   VRMA が反映される前（＝T ポーズのまま）の bounds を採ってしまう。
        ///   VRMA の完了を購読する手段が無い以上、<b>読み込み後の数秒間だけ毎秒測り直して
        ///   間接的に拾う</b>のが素直な形。
        /// ★ <b>毎フレーム測らないこと。</b> ボーン数十個の読み取り自体は軽くても、
        ///   常駐アプリで理由なく走らせ続けない。
        /// ★ <b><see cref="VrmBounds.IsFramingBone"/> で腕を bounds から外した後もこの窓は残す。</b>
        ///   初回の値がもう「肩幅」で正しくなる（ポップは消える）だけで、髪や裾の spring bone が
        ///   落ち着くまでの微差や、ユーザーが差し替えた別の <c>.vrma</c> を拾う保険は引き続き要る。
        /// </summary>
        private float _boneRecheckDeadline;

        /// <summary>次に測り直す時刻。<see cref="BoneRecheckIntervalSeconds"/> ごとに進む。</summary>
        private float _nextBoneRecheckAt;

        /// <summary>
        /// 直近でログに出したフレーミングの文面。
        /// ★ 測り直しのたびに毎秒同じ内容を出さないための重複排除（<see cref="Frame"/>）。
        /// </summary>
        private string _lastFramingLog;

        /// <summary>読み込み後、ボーンを毎秒測り直す期間（秒）。<c>WindowSizeKeeper</c> の見張り時間と同じ考え方。</summary>
        private const float BoneRecheckWindowSeconds = 5f;

        /// <summary>測り直しの間隔（秒）。</summary>
        private const float BoneRecheckIntervalSeconds = 1f;

        /// <summary>読み込めたモデルのルート。まだなら <c>null</c>。</summary>
        public GameObject Model { get; private set; }

        /// <summary>
        /// 自動フレーミングの余白の係数（設定パネル / #76）。
        ///
        /// ★★ <b>大きいほどキャラが小さい。</b> カメラを後ろへ下げる量なので、
        ///   UI の「大きさ」とは<b>向きが逆</b>。写像は
        ///   <c>Settings.SettingsMapping.HeadroomFor</c> に出してテストで固定してある ——
        ///   ここに直接 UI の値を代入すると、スライダーを右に振るほど小さくなる。
        ///
        /// ★ <b>set で <c>Frame()</c> を直接呼ばないこと。</b> フレーミングは
        ///   <c>LateUpdate</c> の中で、境界の再計測と同じ順序で走る必要がある
        ///   （<c>Remeasure</c> の3点セット）。ここでは<b>次の <c>LateUpdate</c> に
        ///   組み直させる</b>ために、最後にフレーミングした画面サイズの記憶を捨てるだけにする。
        /// </summary>
        public float Headroom
        {
            get { return headroom; }
            set
            {
                // ★ 0 以下を通さないこと。カメラがモデルの中へ入る（クリップして中身が見える）
                var next = Mathf.Max(0.01f, value);
                if (Mathf.Approximately(next, headroom)) return;
                headroom = next;
                _framedWidth = -1;
                _framedHeight = -1;
            }
        }

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

            // ★ **回す前に ControlRig を作らせること。** Vrm10Runtime は遅延生成で、
            //   放っておくと LateUpdate の初回アクセス（SpringBone.RestoreInitialTransform）で
            //   ＝ FaceCamera の**後**に作られる。
            //
            //   Vrm10ControlBone は ControlBone を**ワールド単位回転**で作る（正規化姿勢は
            //   ワールド軸で表される）が、_initialTargetGlobalRotation には実ボーンの
            //   ワールド回転が入る。回した後に作ると後者にだけ 180° が乗り、
            //   ProcessRecursively の Inverse(G) * q * G が 180° ぶん食い違って
            //   **Z 軸まわりの回転（＝腕の上下）が反転する** —— #59 の実機で
            //   「待機モーションで腕が真上に上がったまま」として出た。
            //
            //   先に作れば _initialTargetGlobalRotation は回す前の値になり、
            //   その後 vrmRoot ごと回っても ControlRig は子として一緒に回るので整合する。
            //   ★ UniVRM の ControlRig は「Vrm10Instance の transform が単位回転」を暗黙の
            //     前提にしている。回すのをやめられない以上、順序で辻褄を合わせるしかない。
            _ = instance.Runtime;

            // ★ **bounds より先に回すこと。** ボーンのワールド位置から組む箱も
            //   ワールド軸に沿うので、回したあとで測り直さないとカメラ距離がずれる
            FaceCamera(instance);

            _bounds = MeasureBounds(instance, boneBoundsMarginMeters);
            _collider = AttachCollider(instance.gameObject, _bounds);

            if (placeholder != null) placeholder.SetActive(false);

            Model = instance.gameObject;

            // ★ フレーミングと spring bone のリセットは**次のフレーム**でやる。
            //   ボーンの初期姿勢はロード直後に確定していないことがあり、
            //   spring bone の Verlet はロード中の巨大な deltaTime で髪を吹き飛ばす
            _framePending = true;

            // ★ VRMA はこの後 VrmCharacter が非同期に読み込む。完了を待たずに
            //   Adopt() は終わるので、「しばらく毎秒測り直す」窓をここで開く
            _boneRecheckDeadline = Time.realtimeSinceStartup + BoneRecheckWindowSeconds;
            _nextBoneRecheckAt = 0f;

            foreach (var handler in _handlers) Invoke(handler, Model);
        }

        private void LateUpdate()
        {
            if (_framePending)
            {
                _framePending = false;
                // ★ 読み込み中に積もった deltaTime で髪が吹き飛ぶのを戻す
                _instance?.Runtime?.SpringBone?.RestoreInitialTransform();
                Remeasure();
                _nextBoneRecheckAt = Time.realtimeSinceStartup + BoneRecheckIntervalSeconds;
                return;
            }

            // ★ VRMA（アイドルモーション）はここまでに非同期で読み込まれているかもしれない。
            //   購読する手段が無いので、読み込み後しばらくは毎秒測り直して間接的に拾う
            //   （_boneRecheckDeadline の <summary> 参照）。窓を過ぎたら何もしない
            //   ＝ 常駐アプリで理由なく走らせ続けない。
            if (Model != null && Time.realtimeSinceStartup < _boneRecheckDeadline &&
                Time.realtimeSinceStartup >= _nextBoneRecheckAt)
            {
                _nextBoneRecheckAt = Time.realtimeSinceStartup + BoneRecheckIntervalSeconds;
                Remeasure();
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

        /// <summary>
        /// ボーンから bounds を測り直し、Collider とカメラの両方に反映する。
        ///
        /// ★ <b>片方だけ更新しないこと。</b> <see cref="FitCollider"/> の <c>&lt;summary&gt;</c>
        ///   にある理由と同じで、「見えている範囲」と「掴める範囲」がずれる。
        /// </summary>
        private void Remeasure()
        {
            _bounds = MeasureBounds(_instance, boneBoundsMarginMeters);
            FitCollider(_collider, _bounds);
            Frame();
        }

        /// <summary>
        /// Humanoid ボーンのワールド位置から bounds を測る。
        ///
        /// ★ <b><see cref="VrmBounds.OfBones"/> を呼ぶのはここだけにすること。</b>
        ///   ボーンの選び方（決め打ちの一覧を持たず、取れたものをそのまま使う）を
        ///   ここに閉じ込める。
        /// ★ <b><c>public static</c> にしてあるのは <c>VrmProbe</c>（Editor）に
        ///   <b>同じ関数</b>を呼ばせるため。</b> あちらの出力は
        ///   <c>Tests/Editor/VrmFramingTests.cs</c> の定数の出所なので、ボーンの選び方を
        ///   別実装に分岐させると<b>ランタイムがもう生成しない数値</b>をテストが守り始める
        ///   （#59 で <c>VrmBounds.Of</c> から切り替えたときに実際に起きた）。
        ///   コピーして揃えるのではなく、呼び先を1つにすること。
        /// ★ <b>決め打ちのボーン一覧を持たない。</b> <c>Chest</c> / <c>UpperChest</c> /
        ///   <c>Toes</c> などの任意ボーンはモデルによって存在せず、<c>TryGetBoneTransform</c>
        ///   が <c>false</c> を返す。<c>HumanBodyBones</c> を全列挙して取れたものだけを
        ///   使えば、モデルごとに存在有無をガードする必要が無い。
        /// ★ <b><c>Enum.GetValues</c> を直に呼ばないこと。</b> 毎回 <c>Array</c> を新規確保し、
        ///   非ジェネリックな <c>foreach</c> なので列挙値がボックス化される。UniVRM 自身が
        ///   <c>CachedEnum</c>（<c>VrmAnimationImporter.GetHumanMap</c>）で避けている。
        /// </summary>
        public static Bounds MeasureBounds(Vrm10Instance instance, float marginMeters)
        {
            if (instance == null) return new Bounds();

            var positions = new List<Vector3>();
            foreach (var bone in CachedEnum.GetValues<HumanBodyBones>())
            {
                if (!VrmBounds.IsFramingBone(bone)) continue;
                if (instance.TryGetBoneTransform(bone, out var t) && t != null)
                {
                    positions.Add(t.position);
                }
            }

            if (positions.Count == 0)
            {
                // ★ VRM 1.0 は Humanoid 必須（Vrm10Instance に [RequireComponent(Humanoid)]）
                //   なので通常は起きないが、起きたときに黙って原点の点を返さないよう警告する
                Debug.LogWarning("[Mascot] Humanoid ボーンが1つも取れないので bounds を測れませんでした");
                return new Bounds();
            }

            return VrmBounds.OfBones(positions, marginMeters);
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
            //   身長なのかは、これが無いと切り分けられない（#59 で腕が下りると反転する）。
            //   ★ ただし同じ内容は出さない。読み込み後しばらく毎秒 Remeasure() が走るので、
            //   素朴に毎回 Log すると同じ行が並ぶだけになる。文面が変わったときだけ出す
            //   ★ **center も出すこと。** size だけでは、FaceCamera で回す前に測ったか
            //   後に測ったかを判別できない —— 180° 回転では size が不変で、変わるのは
            //   center の x / z の符号だけ（PR #69 の再レビューで判明。「実機のログが
            //   probe の出力と一致した」を根拠にしてしまったが、一致した量が
            //   判別できない量だった）。VrmProbe の "frame bounds center" と
            //   突き合わせられるように、ここにも出しておく
            var message = $"[Mascot] フレーミング: {Screen.width}x{Screen.height} " +
                          $"aspect={camera.aspect:F3} bounds={_bounds.size} center={_bounds.center} " +
                          $"distance={distance:F2} 支配軸={(axis == FramingAxis.Horizontal ? "水平" : "垂直")}";
            if (message != _lastFramingLog)
            {
                Debug.Log(message);
                _lastFramingLog = message;
            }
        }

        /// <summary>
        /// モデルをカメラの方へ向ける。<b>適用したヨー（度）を返す。</b>回さなかった経路は
        /// すべて <c>0f</c>。
        ///
        /// ★ <b>実機で背中が映った。</b> #56 の issue 本文は「glTF→Unity の Z 反転で
        ///   モデルが −Z を向くから 180°回転は不要」としていたが、そうならなかった。
        ///   仕様の読みに頼らず、<b>肩の並びから実際の向きを出して回す</b>
        ///   （→ <see cref="VrmOrientation"/>）。
        ///
        /// ★ <b>照明は回さない。</b> シーンの Directional Light は −Z 側（カメラ側）を
        ///   照らす向きに置いてあるので、モデルの正面をカメラへ向ければ顔に光が当たる。
        ///
        /// ★ <b><c>public static</c> にしてあるのは <c>VrmProbe</c>（Editor）に同じ
        ///   staging をさせるため。</b> <see cref="MeasureBounds"/> を共有しただけでは
        ///   足りず、<b>入力の姿勢（回した後かどうか）も揃えないと同じ箱にならない</b>
        ///   ——ボーンのワールド位置から組む箱はワールド軸に沿うので、向きが違えば
        ///   別の bounds になる（PR #69 の再レビューで判明）。戻り値はその
        ///   「実際に適用したヨー」で、probe が出力に載せるためのもの。
        /// </summary>
        public static float FaceCamera(Vrm10Instance instance)
        {
            var animator = instance.GetComponent<Animator>();
            if (animator == null || animator.avatar == null) return 0f;

            var left = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var right = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (left == null || right == null)
            {
                Debug.LogWarning("[Mascot] 上腕のボーンが無いので向きを判定できません");
                return 0f;
            }

            var yaw = VrmOrientation.YawToFaceCamera(left.position, right.position);
            if (Mathf.Approximately(yaw, 0f)) return 0f;

            instance.transform.Rotate(Vector3.up, yaw, Space.World);
            Debug.Log($"[Mascot] モデルの向きを {yaw:F0} 度回してカメラへ向けました");
            return yaw;
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
