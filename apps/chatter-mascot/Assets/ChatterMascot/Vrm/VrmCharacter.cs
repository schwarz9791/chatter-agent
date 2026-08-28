using System;
using System.Threading;
using ChatterMascot.Protocol;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 読み込まれた VRM に「体」（アイドルモーション）と「目」（視線）を与える。
    ///
    /// <b>実行順は既定（0）のまま。</b> <see cref="Vrm10Instance"/> の <c>LateUpdate</c> は
    /// <c>[DefaultExecutionOrder(11000)]</c> なので、既定の 0 で足りる ——
    /// このコンポーネントの <c>LateUpdate</c> が先に <c>ControlRig</c> / <c>gazeTarget</c> へ書き、
    /// そのあと <see cref="Vrm10Runtime.Process"/> がそれを実ボーンへ反映する。
    ///
    /// <b>表情（Expression チャンネル）は一切触らない。</b> #57 が「対応表は1箇所」で持つ。
    /// prompt の前傾と、視線から求めた頭の角度を<b>実ボーンに乗せる</b>のは
    /// <see cref="VrmPoseAccent"/>（実行順 11005）の役目 —— ここは <c>Kind</c> / <c>Speaking</c> /
    /// <c>Emotion</c> / <c>HeadPitchDegrees</c> / <c>HeadYawDegrees</c> を計算して読ませるだけ。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VrmCharacter : MonoBehaviour
    {
        [Header("配線")]
        [Tooltip("読み込み完了の通知元")]
        [SerializeField] private VrmStage stage;

        [Tooltip("再生状態の読み取り元。未設定なら FindFirstObjectByType で拾う")]
        [SerializeField] private MascotRunner runner;

        [Tooltip("視線が向く先。未設定なら Camera.main の子に作る")]
        [SerializeField] private Transform gazeTarget;

        [Header("挙動")]
        [Tooltip("VRMA が無いときに手続き的アイドル（呼吸・重心移動・首の微動）を回すか")]
        [SerializeField] private bool proceduralIdle = true;

        [Tooltip("マウスカーソルを目で追うか。false または CursorProvider が無ければ自律的な漂いに倒れる")]
        [SerializeField] private bool cursorGaze = true;

        [Tooltip("カーソル方向へ目標位置を動かす感度。cc-mascot の既定値（0.4）を踏襲")]
        [SerializeField] private float eyeSensitivity = 0.4f;

        [Tooltip("カーソル方向へ頭を向ける感度。cc-mascot の既定値（0.1）を踏襲")]
        [SerializeField] private float headSensitivity = 0.1f;

        /// <summary>
        /// カメラが体の中心（腰のあたり）にあるぶん、頭を下へ向ける割合。<see cref="VrmPoseAccent"/> が読む。
        ///
        /// ★ <b>幾何的に正しいのは 1.0。</b> 実測（同梱 vita.vrm、目の高さ約 1.38m・カメラの高さ約
        ///   0.78m・カメラまでの水平距離約 1.75m）で、1.0 のとき頭は約 19° 下を向く —— これは
        ///   「見る人の目の高さちょうどを見る」ために幾何的に必要な角度そのもので、導出は誤っていない。
        /// ★ <b>既定を 0.6（約 11.4°）にしているのは見た目の判断。</b> 実機で 1.0 のスクリーンショットを
        ///   見ると、カメラが体の中心にあるぶんを幾何的に完全補正すると見る人の胸のあたりを見る形になり、
        ///   視線が落ちすぎに見えた。0.6 は「幾何が間違っている」から下げたのではなく、
        ///   「幾何的に正しい角度は見た目には強すぎる」という体感で調整した値 —— この区別を消さないこと。
        /// ★ <b>つまみはここ（<see cref="VrmCharacter"/>）に置くこと。</b> <c>VrmPoseAccent</c> は
        ///   <c>VrmCharacter.OnLoaded</c> が実行時に <c>AddComponent</c> で生やすコンポーネントなので、
        ///   あちらの <c>[SerializeField]</c> はシーンにシリアライズされず、Inspector から一切
        ///   変更できない（フィールド初期化子が常に使われる）。<c>SceneFixups</c> が
        ///   <c>Mascot.unity</c> に結線しているのはこちら側なので、調整の口はこちらに持たせる。
        /// </summary>
        [Header("視線の中立")]
        [Tooltip("カメラが体の中心（腰のあたり）にあるぶん、頭を下へ向ける割合。1 で幾何的に「見る人を見る」。0 で無効")]
        [SerializeField] private float neutralAimFraction = 0.6f;

        /// <summary>
        /// Desktop 側（<c>CursorGazeSource</c>）が刺す。<c>null</c> なら自律的な漂いに倒れる。
        ///
        /// ★ <b>Android にはこの注入元が存在しない</b>（<c>ChatterMascot.Desktop</c> アセンブリごと
        ///   コンパイルされない）ので、常に <c>null</c> のまま＝漂いへ自動的に倒れる。
        /// </summary>
        public Func<Vector2?> CursorProvider { get; set; }

        public SpeechKind Kind { get; private set; }
        public bool Speaking { get; private set; }

        /// <summary>#57 が使う。ここでは読み捨てる（表情には一切触らない）。</summary>
        public Emotion Emotion { get; private set; }

        /// <summary><see cref="VrmPoseAccent"/> が読む、視線由来の頭の角度（緩和済み・度）。</summary>
        public float HeadPitchDegrees { get; private set; }

        /// <summary><see cref="VrmPoseAccent"/> が読む、視線由来の頭の角度（緩和済み・度）。</summary>
        public float HeadYawDegrees { get; private set; }

        /// <summary>
        /// <see cref="VrmPoseAccent"/> が読む、頭に足す「基準の下向き」の係数。
        /// ★ <b>つまみをあちらに置かないこと。</b> <c>VrmPoseAccent</c> は実行時に
        ///   <c>AddComponent</c> で生やすので、<c>[SerializeField]</c> がシリアライズされず
        ///   <b>Inspector から一切変更できない</b>（初期化子が常に使われる）。
        ///   シーンに載っているのはこちらなので、調整の口はこちらに置く。
        /// </summary>
        public float NeutralAimFraction => neutralAimFraction;

        /// <summary>
        /// 視線の原点の<b>ビューポート Y</b>（0..1、下が 0）。取れなければ 0.5。
        ///
        /// ★ <see cref="ChatterMascot.Desktop.CursorGazeSource"/> が縦の基準に使う。
        ///   cc-mascot の <c>useCursorTracking.ts</c> が <c>mouse.y - headY</c> としているのと
        ///   同じ趣旨で、<b>「顔の高さ」を視線の中立にする</b>ため。ウィンドウの中心はキャラの
        ///   腰のあたりなので、そこを基準にすると顔の高さでも上を向いてしまう
        ///   （実機で「顔の横にカーソルを置いてもやや上目線」として発覚）。
        /// </summary>
        public float GazeOriginViewportY { get; private set; } = 0.5f;

        /// <summary>
        /// <see cref="GazeOriginViewportY"/> が実測で埋まったか。
        ///
        /// ★ 埋まる前の <c>0.5</c> はフォールバックで、視線の中立を表していない。
        ///   <see cref="ChatterMascot.Desktop.CursorGazeSource"/> はこれを見ずに
        ///   診断ログを出すと、初期値がそれらしい数字として1回だけ記録されて
        ///   「確かめた」と誤解される（実機で踏んだ ——「動いて見える死体」の形）。
        /// </summary>
        public bool HasGazeOrigin { get; private set; }

        private Vrm10Instance _instance;
        private VrmIdleAnimation _idle;
        private CancellationTokenSource _cancellation;
        private Vector3 _hipsRestLocalPosition;
        private bool _hipsRestCaptured;
        private bool _warnedNoGazeOrigin;
        private Vector3 _gazeOriginWorld;
        private bool _gazeOriginValid;

        /// <summary>
        /// ★ <b>毎フレーム <c>Camera.main</c> を引かないこと。</b>
        ///   <c>CursorGazeSource</c> / <c>VrmPoseAccent</c> と同じ理由（タグ検索でシーン走査が走り、
        ///   常駐アプリの電力予算に効く）。<see cref="Start"/> で1回だけキャッシュする。
        ///
        /// ★ <c>Transform</c> ではなく <c>Camera</c> で持つこと。視線の中立位置（目の高さ）を
        ///   ビューポート座標へ落とす <see cref="Camera.WorldToViewportPoint"/> が要る。
        /// </summary>
        private Camera _camera;

        /// <summary>
        /// <see cref="Start"/> で1回だけ引いた <see cref="Camera.main"/> の読み取り専用公開。
        ///
        /// ★ <b><see cref="VrmPoseAccent"/> が自分で <c>Camera.main</c> を引かないため。</b>
        ///   探索は1箇所（ここ）に寄せ、<c>MainCamera</c> タグが <see cref="Start"/> と
        ///   <c>VrmPoseAccent.Bind</c> の間で付け替わっても、2つのコンポーネントが
        ///   別々のカメラを掴まないようにする。
        /// </summary>
        public Camera Camera => _camera;

        private void Start()
        {
            if (runner == null) runner = FindFirstObjectByType<MascotRunner>();

            _camera = Camera.main;
            if (_camera != null)
            {
                if (gazeTarget == null)
                {
                    var go = new GameObject("GazeTarget");
                    go.transform.SetParent(_camera.transform, false);
                    gazeTarget = go.transform;
                }
            }
            else
            {
                // ★ 起動順序次第で普通に起こりうる（Camera.main はタグ検索なので、
                //   カメラ自体はあっても MainCamera タグが外れていると null になる）。
                //   LateUpdate は _camera == null をガードするので、続行できる異常として警告に留める。
                //   GazeTarget が作れないだけでなく、視線の中立位置（目の高さ）も出せなくなる
                Debug.LogWarning("[Mascot] Camera.main が無いので GazeTarget を作れません。視線は動きません");
            }

            _cancellation = new CancellationTokenSource();

            // ★ sticky なので購読と読み込みの前後関係に依存しない（VrmStage.AddLoadedHandler）
            if (stage != null)
            {
                stage.AddLoadedHandler(OnLoaded);
            }
            else
            {
                Debug.LogWarning("[Mascot] VrmCharacter に VrmStage が結線されていません");
            }
        }

        private void OnLoaded(GameObject model)
        {
            _instance = model.GetComponent<Vrm10Instance>();
            if (_instance == null)
            {
                Debug.LogWarning("[Mascot] 読み込まれたモデルに Vrm10Instance が無いので、体と目の配線を諦めます");
                return;
            }

            // ★ これを忘れると視線が一切動かず、エラーも出ない
            _instance.LookAtTargetType = VRM10ObjectLookAt.LookAtTargetTypes.SpecifiedTransform;
            _instance.LookAtTarget = gazeTarget;

            var accent = model.AddComponent<VrmPoseAccent>();
            accent.Bind(_instance, this);

            // ★ hips の静止位置はここでは取らない。ControlRig（Vrm10Instance.Runtime.ControlRig）は
            //   遅延生成なので、ここで確実に非 null とは限らない。UpdateProceduralIdle 側で
            //   ControlBone から遅延して取る（TryGetBoneTransform の実ボーンとは親が別なので
            //   混ぜてはいけない）。

            // ★ 例外は VrmIdleAnimation.LoadAsync の内側で全部握る（VrmStage.Start と同じ形）
            _idle = new VrmIdleAnimation();
            _ = _idle.LoadAsync(_instance, transform, _cancellation.Token);
        }

        private void LateUpdate()
        {
            // ★ Time.realtimeSinceStartupAsDouble を使うこと。DateTimeOffset.UtcNow は
            //   時計が巻き戻るとアイドルが凍る（AudioIdleGate と同じ理由）
            var now = Time.realtimeSinceStartupAsDouble;

            // ★ kind / emotion を先に既定値で確定させること。runner == null のときは
            //   && の短絡で TryGetSpeaking 自体が呼ばれず out に何も入らないので、
            //   `out var` で受けると CS0165（未割り当てローカル変数の使用）になる。
            // ★ false のときに既定値へ倒すのは呼ばれた側（SpeakingView.TryRead）の契約で、
            //   ここはそれに乗っている。**この契約は SpeakingViewTests が固定している**
            //   （ReturnsFalseWhenNothingIsPlaying / ReturnsFalseWhenOnlyOrphansArePlaying /
            //   DoesNotThrowWhenStateIsNull / DoesNotThrowWhenThePlayingItemHasNoRecord の
            //   4本が、false のとき Assistant / Neutral になることを検査している）。
            //   だからここで `Speaking ? kind : 既定` と書き直す必要はない。
            var kind = SpeechKind.Assistant;
            // ★ 完全修飾で書くこと。この型は自分自身と同名のプロパティ Emotion を持つので、
            //   ここで単に `Emotion.Neutral` と書くとプロパティ側に解決されてしまい、
            //   「インスタンス参照で static メンバーにアクセスしている」というコンパイルエラーになる
            var emotion = ChatterMascot.Protocol.Emotion.Neutral;
            Speaking = runner != null && runner.TryGetSpeaking(out kind, out emotion);
            Kind = kind;
            Emotion = emotion;

            // ★ UpdateGaze の中に置かないこと。UpdateGaze は先頭で gazeTarget == null ||
            //   _camera == null を早期 return するので、そこに置くとカメラが無いときに
            //   キャッシュが更新されない。「同じ点を使う」（TryGetCachedGazeOrigin の doc）を
            //   成立させるため、1フレームに1回だけここで測る
            _gazeOriginValid = TryGetGazeOrigin(out _gazeOriginWorld);

            UpdateGaze(now);

            if (_instance == null) return;
            // ★ VRMA が有効なら手続き的は動かさない。ControlRig の奪い合いになる
            if (_idle != null && _idle.IsPlaying) return;
            if (!proceduralIdle) return;

            UpdateProceduralIdle(now);
        }

        private void UpdateGaze(double now)
        {
            if (gazeTarget == null || _camera == null) return;

            var gazeParams = GazeParamsFromInspector();
            var cursor = cursorGaze && CursorProvider != null ? CursorProvider() : null;
            var sample = GazeAim.Evaluate(now, gazeParams, Kind, cursor);

            // ★ 中立は「カメラの位置」（gazeTarget は Camera.main の子なので Vector3.zero）。
            //   以前はここを「カメラの正面、目の高さ」まで動かして直そうとしたが、それでは
            //   頭が水平を向いたまま目だけを大きく下げることになり、UniVRM の LookAt
            //   （目ボーンだけを、目標角のごく一部しか動かさない）では下げ切れなかった
            //   （実機で「顔の横にカーソルを置いてもやや上目線」）。いまは VrmPoseAccent が
            //   頭そのものをカメラへ向ける「基準の下向き」を持つので、頭がカメラを向いた状態で
            //   目標もカメラなら目の残差はゼロ＝目が中立になる。動かすのは頭であって、
            //   目標をずらすことではない。
            // ★ ここは「出力」の中立（gazeTarget が指す位置）の話。カーソル入力側の縦の基準
            //   （下の GazeOriginViewportY、cc-mascot の mouse.y - headY と同じ趣旨）は
            //   「顔の高さ」のまま変えていない —— 入力の基準と出力の中立は別物なので
            //   混同しないこと（「カーソルが顔の高さ」＝「目標がカメラ」＝「見る人と目が合う」）。
            var neutralLocal = Vector3.zero;

            if (_gazeOriginValid)
            {
                GazeOriginViewportY = _camera.WorldToViewportPoint(_gazeOriginWorld).y;
                HasGazeOrigin = true;
            }
            else
            {
                GazeOriginViewportY = 0.5f;
                HasGazeOrigin = false;
                // ★ _instance が入る（OnLoaded を通る）まではここを毎フレーム必ず通る
                //   （LateUpdate はフレーム1から走るが、VRM の読み込みは実測で約1.6秒かかる）。
                //   そこで無条件に警告すると、「VRM が読めているかどうかに関わらず毎回1回だけ
                //   出るログ」になり、異常検知として機能しない（読み飛ばす癖がつくぶん有害）。
                //   モデルが読み込まれた後もなお原点が取れない場合だけが本当の異常なので、
                //   _instance != null を条件に足す（CursorGazeSource が同じ形の失敗
                //   ——値が入る前に発火して初期値だけを記録する「動いて見える死体」——
                //   を一度踏んで直したのと同じ考え方）。
                if (_instance != null && !_warnedNoGazeOrigin)
                {
                    _warnedNoGazeOrigin = true;
                    Debug.LogWarning("[Mascot] 視線の原点（目 / 頭ボーン）が取れないので、GazeOriginViewportY をフォールバック値にします");
                }
            }

            var target = neutralLocal + new Vector3(sample.TargetLocalPosition.x, sample.TargetLocalPosition.y, 0f);

            // ★ Time.deltaTime ではなく Time.unscaledDeltaTime を使うこと。位相（now）は
            //   Time.realtimeSinceStartupAsDouble で回しているので、緩和も同じ時間軸で
            //   回さないと Time.timeScale = 0 で緩和だけが凍り、目標値は realtime で進み続け、
            //   timeScale が戻った瞬間に頭と前傾が飛ぶ（spring bone を跳ねさせないために
            //   避けている失敗そのもの）。
            var dt = Time.unscaledDeltaTime;
            var current = gazeTarget.localPosition;
            gazeTarget.localPosition = new Vector3(
                GazeAim.Smooth(current.x, target.x, dt, gazeParams.FollowSeconds),
                GazeAim.Smooth(current.y, target.y, dt, gazeParams.FollowSeconds),
                GazeAim.Smooth(current.z, target.z, dt, gazeParams.FollowSeconds));

            HeadPitchDegrees = GazeAim.Smooth(HeadPitchDegrees, sample.HeadPitchDegrees, dt, gazeParams.FollowSeconds);
            HeadYawDegrees = GazeAim.Smooth(HeadYawDegrees, sample.HeadYawDegrees, dt, gazeParams.FollowSeconds);
        }

        /// <summary>
        /// 視線の原点（ワールド）。<b>目ボーンがあればその中点、無ければ Head</b>。
        ///
        /// ★ <c>private</c>。<see cref="LateUpdate"/> が1フレームに1回だけ呼び、結果を
        ///   <c>_gazeOriginWorld</c> にキャッシュする。<see cref="VrmPoseAccent"/> はここを
        ///   直接呼ばず、<see cref="TryGetCachedGazeOrigin"/> でそのキャッシュを読む
        ///   —— 理由は <see cref="TryGetCachedGazeOrigin"/> の doc を参照。
        /// ★ <c>LeftEye</c> / <c>RightEye</c> は任意ボーン。<c>vita.vrm</c> は持っているが、
        ///   持たないモデルもあるので必ず <c>Head</c> へのフォールバックを用意する。
        /// ★ <c>Head</c> フォールバックでは <c>Vrm.LookAt.OffsetFromHead</c>
        ///   （既定 <c>(0, 0.06, 0)</c>）を head ボーンのローカル空間で足す
        ///   （<c>Vrm10RuntimeLookAt.InitializeLookAtOriginTransform</c> の
        ///   <c>humanoid.Head.TransformPoint(eyeOffsetValue)</c> と同じ式）。
        ///   <c>Vrm</c> / <c>LookAt</c> は <c>null</c> のことがあるのでガードする。
        /// </summary>
        private bool TryGetGazeOrigin(out Vector3 world)
        {
            world = default;
            if (_instance == null) return false;

            var hasLeft = _instance.TryGetBoneTransform(HumanBodyBones.LeftEye, out var left);
            var hasRight = _instance.TryGetBoneTransform(HumanBodyBones.RightEye, out var right);

            if (hasLeft && hasRight)
            {
                world = (left.position + right.position) * 0.5f;
                return true;
            }
            if (hasLeft)
            {
                world = left.position;
                return true;
            }
            if (hasRight)
            {
                world = right.position;
                return true;
            }

            if (!_instance.TryGetBoneTransform(HumanBodyBones.Head, out var head)) return false;

            var offset = _instance.Vrm != null && _instance.Vrm.LookAt != null
                ? _instance.Vrm.LookAt.OffsetFromHead
                : Vector3.zero;
            world = head.TransformPoint(offset);
            return true;
        }

        /// <summary>
        /// このフレームで測った視線の原点（ワールド）。<see cref="VrmPoseAccent"/> が読む。
        ///
        /// ★ <b>「同じ点を使う」を成立させるために、測るのは1フレームに1回だけにすること。</b>
        ///   目ボーンは頭の子なので、<see cref="VrmPoseAccent"/>（実行順 11005）が自分で
        ///   <c>TryGetBoneTransform</c> を引き直すと、<c>ControlRig.Process()</c>（11000）が
        ///   書き戻した後＝アクセント抜きの位置になり、<b>実行順 0 でここが測った
        ///   「前フレームのアクセント込み」の位置とは別の点になる</b>。
        ///   直す理由は挙動ではなく、<b>両方のコメントが宣言している不変条件が
        ///   コード上は成立していなかった</b>こと —— ズレの大きさ自体は、頭ボーンから目までの
        ///   オフセット（<c>vita.vrm</c> で約 0.06m）× 基準の下向き（既定 0.6 で約 11.4°）の
        ///   sin なので<b>1cm ほどと見積もれる</b>（実測はしていない。体感には出ない）。
        /// ★ このキャッシュはアクセント込みの位置なので、<see cref="VrmPoseAccent"/> が
        ///   これを使うと弱い帰還路になる（頭が下を向く → 目が下がる → 次フレームの
        ///   基準の下向きがわずかに小さくなる）。<b>負帰還</b>で、利得は上の 1cm を
        ///   目とカメラの高低差（約 0.6m）で割った程度＝おおむね 0.02 と見積もれるので、
        ///   数フレームで収束する。<b>これも見積もりであって実測ではない</b> ——
        ///   もし視線が微振動するようなら、ここを疑うこと。
        /// </summary>
        public bool TryGetCachedGazeOrigin(out Vector3 world)
        {
            world = _gazeOriginWorld;
            return _gazeOriginValid;
        }

        private void UpdateProceduralIdle(double now)
        {
            var rig = _instance.Runtime?.ControlRig;
            if (rig == null) return;

            var sample = IdlePose.Evaluate(now, IdleParams.Default, Kind, Speaking);

            SetBoneEuler(rig, HumanBodyBones.Spine, sample.SpineEuler);
            // ★ Chest は任意ボーン。モデルによっては無いので GetBoneTransform が null を返す
            SetBoneEuler(rig, HumanBodyBones.Chest, sample.ChestEuler);
            SetBoneEuler(rig, HumanBodyBones.Neck, sample.NeckEuler);
            SetBoneEuler(rig, HumanBodyBones.Head, sample.HeadEuler);
            // ★ Upper/LowerArm は Humanoid の必須ボーンだが、GetBoneTransform が null を返す
            //   ケースはゼロと決め打ちしない。SetBoneEuler の既存ガードをそのまま使う
            SetBoneEuler(rig, HumanBodyBones.LeftUpperArm, sample.LeftUpperArmEuler);
            SetBoneEuler(rig, HumanBodyBones.RightUpperArm, sample.RightUpperArmEuler);
            SetBoneEuler(rig, HumanBodyBones.LeftLowerArm, sample.LeftLowerArmEuler);
            SetBoneEuler(rig, HumanBodyBones.RightLowerArm, sample.RightLowerArmEuler);

            var hips = rig.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) return;

            // ★ 静止位置は ControlBone から取ること。実ボーン（TryGetBoneTransform）とは
            //   親が違う（あちらは glTF 階層、こちらは "Runtime Control Rig" の子）ので、
            //   取り違えると手続き的アイドルの初回フレームで腰が飛ぶ。
            // ★ OnLoaded ではなくここで遅延して取るのは、ControlRig が Runtime の遅延生成に
            //   ぶら下がっているため。
            if (!_hipsRestCaptured)
            {
                _hipsRestLocalPosition = hips.localPosition;
                _hipsRestCaptured = true;
            }

            // ★ Vector3.up（ワールドの上）を localPosition（ローカル空間の値）へそのまま
            //   足さないこと。"Runtime Control Rig" が単位回転のいまはたまたま一致するが、
            //   「たまたま一致している」前提を式に残すと #56 と同じ形で壊れる。
            //   Y 成分への加算だとローカル/ワールドの区別に依存しない
            hips.localPosition = new Vector3(
                _hipsRestLocalPosition.x,
                _hipsRestLocalPosition.y + sample.HipsOffsetY,
                _hipsRestLocalPosition.z);
        }

        /// <summary>
        /// ★ <c>ControlBone</c> は <c>localRotation == identity</c> が T ポーズなので、
        ///   ここは<b>代入で正しい</b>（<see cref="VrmPoseAccent"/> の実ボーンとは違い、乗算ではない）。
        /// </summary>
        private static void SetBoneEuler(Vrm10RuntimeControlRig rig, HumanBodyBones bone, Vector3 euler)
        {
            var t = rig.GetBoneTransform(bone);
            if (t != null) t.localRotation = Quaternion.Euler(euler);
        }

        private GazeParams GazeParamsFromInspector()
        {
            var d = GazeParams.Default;
            return new GazeParams(
                d.WanderSecondsX, d.WanderSecondsY, d.WanderMetersX, d.WanderMetersY,
                eyeSensitivity, headSensitivity, d.HeadPitchRangeDegrees, d.HeadYawRangeDegrees,
                d.EyeReachMeters, d.FollowSeconds);
        }

        /// <summary>
        /// ★ <c>finally</c> が走らない経路（ドメインリロード / シーン破棄）の受け皿。
        ///   <see cref="VrmIdleAnimation.Dispose"/> は冪等なので二重解放にならない
        ///   （<c>VrmStage.OnDisable</c> と同じ形）。
        /// </summary>
        private void OnDisable()
        {
            _cancellation?.Cancel();
            _idle?.Dispose();
            _idle = null;
        }

        private void OnDestroy()
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
