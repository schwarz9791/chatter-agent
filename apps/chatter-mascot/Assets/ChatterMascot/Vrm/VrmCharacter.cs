using System;
using System.Collections.Generic;
using System.Threading;
using ChatterMascot.Protocol;
using UniGLTF.Extensions.VRMC_vrm;
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
    /// <b>「顔」（表情と瞬き）もここが持つ</b>（#57）。判断は <see cref="FacePolicy"/> と
    /// <see cref="BlinkTimer"/>（どちらも <c>ChatterMascot.Runtime</c> の純粋クラス）にあり、
    /// ここは<b><c>FaceWeights</c> を <c>ExpressionKey</c> へ写して <c>SetWeightsNonAlloc</c> する
    /// 1箇所</b>。<c>Expression.Process()</c> は <c>Vrm10Instance.LateUpdate</c>（実行順 11000）の
    /// 中で走るので、実行順 0 のここから書けば同じフレームで反映される。
    ///
    /// prompt の前傾と、視線から求めた頭の角度を<b>実ボーンに乗せる</b>のは
    /// <see cref="VrmPoseAccent"/>（実行順 11005）の役目 —— ここは <c>Kind</c> / <c>Speaking</c> /
    /// <c>Emotion</c> / <c>HeadPitchDegrees</c> / <c>HeadYawDegrees</c> を計算して読ませるだけ。
    /// ★ <b>表情を <c>VrmPoseAccent</c> 側から書かないこと。</b> あちらは 11005 ＝
    ///   <c>Expression.Process()</c> の<b>後</b>なので、書いても1フレーム遅れる。
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

        [Header("表情")]
        [Tooltip("emotion が切り替わるときの片道の指数緩和の時定数（秒）。0 で即時切り替え")]
        [SerializeField] private float expressionLerpSeconds = 0.15f;

        [Tooltip("発話が止まってから Neutral へ戻し始めるまでの猶予（秒）。★ 文と文の分断を埋めるためのもので、短くすると1文ごとに顔が抜ける")]
        [SerializeField] private float faceHoldSeconds = 1.5f;

        [Tooltip("kind: prompt のときに surprised へ上乗せする量。★ 既定 0。上げると emotion と重なって「怒りながら驚いた顔」になる")]
        [SerializeField] private float promptSurpriseWeight;

        [Tooltip("happy の実効 weight がこれを超えている間は瞬きを止める。0 で無効。★ VRoid の happy は目を細める形を含むので、瞬きと素で加算すると破綻する")]
        [SerializeField] private float blinkSuppressAboveHappy = 0.1f;

        [Tooltip("Neutral を neutral expression の weight 1.0 で表すか。★ 既定 false（全部 0）。true にするとモデルによっては別の顔になり、emotion と二重ブレンドになる")]
        [SerializeField] private bool useNeutralExpression;

        [Tooltip("自動まばたきを回すか")]
        [SerializeField] private bool blinkEnabled = true;

        /// <summary>
        /// RMS を口の開きへ写すときの倍率。cc-mascot の <c>min(1.0, rms * 4)</c> と同じ。
        ///
        /// ★ <b>0 にすると口が動かない。</b> 他の調整値と違って「0 = 無効」ではない
        ///   （→ <see cref="MouthTracker.Tick"/>）。
        /// </summary>
        [Tooltip("RMS → 口の開きの倍率（cc-mascot の rms * 4 と同じ）。★ 0 にすると口が動かない")]
        [SerializeField] private float mouthGain = 4f;

        [Tooltip("口を閉じる方向の減衰（毎秒）。0 で無効。★ 30fps で音素の谷ごとに口が閉じて階段に見えるのを均す")]
        [SerializeField] private float mouthReleasePerSecond = 8f;

        // ★ [Range] は「定義域は [0,1]」という doc の宣言を Inspector にも書くための補助。
        //   0..1 に閉じる不変条件そのものは FacePolicy.Evaluate 側にある（あちらは
        //   FaceParams を直接作れるので、ここだけでは守れない）
        [Tooltip("happy が立ち切っているときの口の開きの倍率。0 で無効（1倍）。★ 笑顔で口が開きすぎてメッシュからはみ出るのを防ぐ")]
        [Range(0f, 1f)]
        [SerializeField] private float mouthScaleHappy = 0.2f;

        [Tooltip("sad が立ち切っているときの口の開きの倍率。0 で無効（1倍）")]
        [Range(0f, 1f)]
        [SerializeField] private float mouthScaleSad = 0.5f;

        [Tooltip("今の emotion / kind と実効 weight を1秒ごとにログへ出す。★ ビルド済みアプリでは起動引数 -faceLog 1 でも立てられる")]
        [SerializeField] private bool faceDebugLog;

        /// <summary>
        /// Desktop 側（<c>CursorGazeSource</c>）が刺す。<c>null</c> なら自律的な漂いに倒れる。
        ///
        /// ★ <b>Android にはこの注入元が存在しない</b>（<c>ChatterMascot.Desktop</c> アセンブリごと
        ///   コンパイルされない）ので、常に <c>null</c> のまま＝漂いへ自動的に倒れる。
        /// </summary>
        /// <summary>
        /// 待機モーションを回すか（設定パネル / #76）。
        ///
        /// ★★ <b>VRMA と手続き的アイドルの<u>両方</u>に効く。</b> あの2実装は
        ///   「片方が読めないときのフォールバック」でしかなく、ユーザーから見て
        ///   「待機モーション」は1つの概念（→ <see cref="VrmIdleAnimation.Enabled"/>）。
        ///   片方だけ止めると「チェックを外したのに動き続ける」になる。
        /// </summary>
        public bool IdleMotion
        {
            get { return proceduralIdle; }
            set
            {
                proceduralIdle = value;
                if (_idle != null) _idle.Enabled = value;
            }
        }

        /// <summary>マウスカーソルを目で追うか（設定パネル / #76）</summary>
        public bool CursorGazeEnabled
        {
            get { return cursorGaze; }
            set { cursorGaze = value; }
        }

        /// <summary>自動まばたきを回すか（設定パネル / #76）</summary>
        public bool BlinkEnabled
        {
            get { return blinkEnabled; }
            set { blinkEnabled = value; }
        }

        public Func<Vector2?> CursorProvider { get; set; }

        public SpeechKind Kind { get; private set; }
        public bool Speaking { get; private set; }

        /// <summary>
        /// いま再生中の発話に載っていた emotion。<b>喋っていない間は <c>Neutral</c>。</b>
        ///
        /// ★ <b>これをそのまま <see cref="FacePolicy"/> へ渡さないこと。</b>
        ///   <c>SpeakingSet.TryGetFace</c> は false のとき <c>Neutral</c> に倒す契約なので、
        ///   生の値を渡すと<b>喋り終わった瞬間に目標が Neutral になり、
        ///   <c>faceHoldSeconds</c> がまったく効かない</b>。表情には
        ///   <see cref="FaceLatch"/> が保つ「発話中に読んだ最後の値」を使う。
        /// </summary>
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

        /// <summary>
        /// <c>ExpressionKey</c> は <c>readonly struct</c> で、静的プロパティは呼ぶたびに
        /// 新しい値を作る（<c>CreateFromPreset</c>）。毎フレーム引く場所なのでまとめておく。
        /// </summary>
        private static readonly ExpressionKey HappyKey = ExpressionKey.Happy;
        private static readonly ExpressionKey AngryKey = ExpressionKey.Angry;
        private static readonly ExpressionKey SadKey = ExpressionKey.Sad;
        private static readonly ExpressionKey RelaxedKey = ExpressionKey.Relaxed;
        private static readonly ExpressionKey SurprisedKey = ExpressionKey.Surprised;
        private static readonly ExpressionKey NeutralKey = ExpressionKey.Neutral;
        private static readonly ExpressionKey AaKey = ExpressionKey.Aa;
        private static readonly ExpressionKey BlinkKey = ExpressionKey.Blink;

        /// <summary>
        /// このアプリが実際に weight を書く preset。<b>これ以外は 0 のまま。</b>
        ///
        /// ★ <see cref="WarnAboutOverrides"/> がこの集合に絞るために要る。
        ///   <c>DefaultExpressionValidator</c> は override 率を
        ///   <c>GetOverrideRate(clip.Override*, weight)</c> で出すので、
        ///   <b>weight が常に 0 のクリップは寄与 0 ＝ 何もブロックできない</b>。
        /// </summary>
        private static readonly ExpressionKey[] DrivenKeys =
        {
            HappyKey, AngryKey, SadKey, RelaxedKey, SurprisedKey, NeutralKey, AaKey, BlinkKey,
        };

        /// <summary>デバッグログの間引き（秒）</summary>
        private const double DefaultFaceLogIntervalSeconds = 1.0;

        /// <summary>
        /// <see cref="LogFace"/> の間引き。<b>起動引数 <c>-faceLogMs</c> で縮められる。</b>
        ///
        /// ★ <b>既定 1秒を縮めないこと。</b> 常用のデバッグログなので、細かくすると
        ///   <c>Player.log</c> が流れて他の診断が読めなくなる。
        /// ★ <b>縮めたくなるのは実機確認のときだけ。</b> #58 の「孤児が重なっている間も
        ///   口が止まらないこと」は <c>aa=</c> の連続で判定するが、1秒間隔では
        ///   <b>サンプルが粗すぎて判定にならない</b>（瞬きが 0.19 秒で終わるのに
        ///   捕捉率が2割しかない、というのと同じ問題）。
        /// </summary>
        private double _faceLogIntervalSeconds = DefaultFaceLogIntervalSeconds;

        private Vrm10Instance _instance;
        private VrmIdleAnimation _idle;
        private CancellationTokenSource _cancellation;
        private Vector3 _hipsRestLocalPosition;
        private bool _hipsRestCaptured;
        private bool _warnedNoGazeOrigin;
        private Vector3 _gazeOriginWorld;
        private bool _gazeOriginValid;

        /// <summary>
        /// ★ <b><see cref="Start"/> ではなくフィールド初期化子で作ること。</b>
        ///   <see cref="LateUpdate"/> は <see cref="Start"/> の後に走る決まりだが、
        ///   その順序に寄りかからないほうが安い（<c>null</c> ガードを毎フレーム書かずに済む）。
        /// ★ <c>UnityEngine.Random</c> と完全修飾すること。このファイルは <c>using System;</c> を
        ///   持つので、素の <c>Random</c> は <c>System.Random</c> と曖昧になる（CS0104）。
        /// </summary>
        private readonly BlinkTimer _blink = new BlinkTimer(() => UnityEngine.Random.value);

        private FaceWeights _faceWeights;

        /// <summary>
        /// 「いま鳴っているもの」から「顔が使う値」への変換の記憶（ラッチと prompt のエッジ）。
        ///
        /// ★ <b>ここをフィールドの寄せ集めとして書かないこと。</b> <c>ChatterMascot.Tests.asmdef</c> は
        ///   <c>ChatterMascot.Runtime</c> しか参照しないので、<c>MonoBehaviour</c> の中に書くと
        ///   <b>テストが1行も当たらない</b> —— しかもここは「これが無いと <c>faceHoldSeconds</c> が
        ///   1行も効かない」と分かっている場所（#57 のレビュー指摘）。
        /// </summary>
        private readonly FaceLatch _faceLatch = new FaceLatch();

        /// <summary>
        /// 口の開きの整形（区間の始点・ゲイン・attack/release）。
        ///
        /// ★ <see cref="_faceLatch"/> と同じ理由で <c>Runtime/</c> 側の純粋クラスに出してある。
        ///   <b>ここをフィールドの寄せ集めに戻さないこと</b> —— テストが1行も当たらなくなる。
        /// </summary>
        private readonly MouthTracker _mouth = new MouthTracker();

        private double _faceLoggedAt = double.NegativeInfinity;

        /// <summary>
        /// <c>SetWeightsNonAlloc</c> に渡す入れ物。<b>使い回すこと。</b>
        ///
        /// ★ <c>SetWeights</c>（<c>IEnumerable</c> 版）だと <c>Dictionary</c> の列挙子が
        ///   ボックス化する。30回/秒 で回るので、常駐アプリの GC 予算に効く。
        /// ★ <c>ExpressionKey.Comparer</c> を渡すこと（UniVRM 内部の辞書と同じ形）。
        /// </summary>
        private readonly Dictionary<ExpressionKey, float> _faceBuffer =
            new Dictionary<ExpressionKey, float>(ExpressionKey.Comparer);

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

            // ★ ビルドした .app からデバッグログを立てられるようにする（-serverUrl / -vrm と同じ形）。
            //   実機確認は Player.log を読む形なので、[SerializeField] だけだと再ビルドが要る
            // ★ Argument ではなく Flag。Argument は「name の次に来る値」しか返さないので、
            //   -faceLog を単独で渡すと null になり、**いちばん自然な渡し方で黙って無反応**になる
            //   （切り分け中に踏むと「ログが出ない＝コードが走っていない」と誤読しかねない）
            var faceLog = CommandLine.Flag("-faceLog", faceDebugLog);
            if (faceLog != faceDebugLog)
            {
                faceDebugLog = faceLog;
                Debug.Log($"[Mascot] faceDebugLog をコマンドラインで上書きします: {faceDebugLog}");
            }

            // ★ こちらは値を取るので Flag ではなく Argument（-faceLogMs 100）。
            //   読めない値は黙って無視する —— 実機確認の道具なので、打ち間違いで
            //   アプリが止まるほうが困る
            var faceLogMs = CommandLine.Argument("-faceLogMs");
            int faceLogMsValue;
            if (!string.IsNullOrEmpty(faceLogMs) &&
                int.TryParse(faceLogMs, out faceLogMsValue) && faceLogMsValue > 0)
            {
                _faceLogIntervalSeconds = faceLogMsValue / 1000.0;
                Debug.Log($"[Mascot] face ログの間隔をコマンドラインで上書きします: {faceLogMsValue}ms");
            }

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
            // ★ 設定を読み込みの**前**に反映しておくこと。ここを飛ばすと、
            //   チェックを外してあるのに VRMA の再生が始まる（読み込みは非同期なので、
            //   後から止めるまでの数秒だけ動く、という気づきにくい形で出る）
            _idle.Enabled = proceduralIdle;
            _ = _idle.LoadAsync(_instance, transform, _cancellation.Token);

            // ★★ 診断は**最後**に呼ぶこと。ここは VrmStage.Invoke に呼ばれていて、
            //   あちらは購読者の例外を LogWarning で握る。途中に置くと、診断が何かで throw した瞬間に
            //   **この下の設定（アイドルモーションの読み込み）が丸ごと走らなくなる** ——
            //   しかも出るのは1行の警告だけで、同梱 VRMA が読まれない理由は残らない。
            //   純粋な観測を本体の道連れにしない。
            // ★ ここ（_instance が入った後）で出すこと自体は変えない。LateUpdate はフレーム1から
            //   走るが読み込みは実測で約1.6秒かかるので、起動直後に判定するとラッチが消費されて
            //   永久に本当の値が出ない（docs/mascot.md「起動直後にだけ成立しない状態を
            //   『異常』として警告しない」）。
            // ★ なお「Runtime の遅延生成がここで走って Head 欠落で throw する」ことは無い ——
            //   VrmStage.Adopt が handler を呼ぶ前に `_ = instance.Runtime;` を済ませている
            //   （ControlRig の生成順のため）。それでも順序を直すのは、道連れにする**構造**の方を
            //   残さないため。
            LogExpressionDiagnostics();
        }

        private void LateUpdate()
        {
            // ★ Time.realtimeSinceStartupAsDouble を使うこと。DateTimeOffset.UtcNow は
            //   時計が巻き戻るとアイドルが凍る（AudioIdleGate と同じ理由）
            var now = Time.realtimeSinceStartupAsDouble;

            // ★ kind / emotion を先に既定値で確定させること。runner == null のときは
            //   && の短絡で TryGetSpeaking 自体が呼ばれず out に何も入らないので、
            //   `out var` で受けると CS0165（未割り当てローカル変数の使用）になる。
            // ★ false のときに既定値へ倒すのは呼ばれた側（SpeakingSet.TryGetFace）の契約で、
            //   ここはそれに乗っている。**この契約は SpeakingSetTests が固定している**。
            //   だからここで `Speaking ? kind : 既定` と書き直す必要はない。
            // ★ #58 以降、Speaking は**孤児（採番のやり直しで鳴らし切っている音）も含む**。
            //   以前の SpeakingView は Orphans に Record が無いので孤児の間 false を返していた。
            var kind = SpeechKind.Assistant;
            // ★ 完全修飾で書くこと。この型は自分自身と同名のプロパティ Emotion を持つので、
            //   ここで単に `Emotion.Neutral` と書くとプロパティ側に解決されてしまい、
            //   「インスタンス参照で static メンバーにアクセスしている」というコンパイルエラーになる
            var emotion = ChatterMascot.Protocol.Emotion.Neutral;
            Speaking = runner != null && runner.TryGetSpeaking(out kind, out emotion);
            Kind = kind;
            Emotion = emotion;

            // ★★ 下の早期 return（_instance == null / _idle.IsPlaying / !proceduralIdle）より
            //   **前**で呼ぶこと。VRMA が読めている＝通常の状態は `_idle.IsPlaying` で抜けるので、
            //   あとに置くと表情が一度も走らない（同梱 idle_loop.vrma があるので常にそうなる）。
            //   顔と体は別のチャンネルで、体を止める条件で顔まで止めてはいけない
            UpdateFace(now);

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

        /// <summary>
        /// 表情（Expression チャンネル）を1フレーム分進めて VRM へ流し込む。
        ///
        /// ★ <b><see cref="LateUpdate"/> の早期 return より前で呼ぶこと</b>（呼び出し側のコメント参照）。
        /// ★ <b>読み取りを新しく書かないこと。</b> <c>Speaking</c> / <c>Kind</c> / <c>Emotion</c> は
        ///   <see cref="LateUpdate"/> が <c>MascotRunner.TryGetSpeaking</c> 経由で確定済み。ここは使うだけ。
        /// ★ <b>瞬きは <c>_instance</c> が無くても回す。</b> 適用先が無いだけで、時間は進める
        ///   （読み込み中に待ちがリセットされ続けると、読み込み直後に必ず瞬くことになる）。
        /// </summary>
        private void UpdateFace(double now)
        {
            // ★ ラッチ（emotion / kind / 発話の立ち下がり）と prompt のエッジは FaceLatch が持つ。
            //   ここに書くとテストから見えなくなる —— 理由は _faceLatch の doc を参照
            // ★ prompt のエッジで1回だけ Request する。毎フレーム呼ぶと瞬きっぱなしになる。
            //   prompt は1イベント単位で来る（docs/protocol.md）ので、assistant に付く
            //   「seq 連続 / messageId 同一 / ts 同値」の保証は当てにできない
            if (_faceLatch.Update(Speaking, Emotion, Kind, now)) _blink.Request();

            _blink.Enabled = blinkEnabled;
            var blink = _blink.Tick(now);

            // ★★ 口も **_instance の早期 return より前**で進めること（瞬きと同じ理由）。
            //   後ろに置くと、VRM の読み込み中（実測 約1.6秒）ずっと区間の始点が更新されず、
            //   モデルが出た最初のフレームで**数秒ぶんの最大値**を取って口が全開に飛ぶ。
            // ★ 区間 [前フレーム, いま] の最大を取る。点サンプリングにすると、20ms 刻みの
            //   エンベロープを 33.3ms 間隔で読むことになり **4割のフレームを読み飛ばす**
            var rawMouth = runner != null ? runner.Mouth(_mouth.From(now), now) : 0f;
            // ★ Time.deltaTime ではなく unscaledDeltaTime（下の緩和と同じ理由）
            var mouth = _mouth.Tick(rawMouth, now, mouthGain, mouthReleasePerSecond, Time.unscaledDeltaTime);

            if (_instance == null) return;
            var runtime = _instance.Runtime;
            var expression = runtime != null ? runtime.Expression : null;
            if (expression == null) return;

            var input = new FaceInput(
                speaking: Speaking,
                // ★ 生の Emotion / Kind ではなくラッチ済みを渡すこと。生のままだと
                //   SpeakingSet の「false なら既定値へ倒す」契約に当たって猶予が効かない
                emotion: _faceLatch.Emotion,
                kind: _faceLatch.Kind,
                // ★ 整形済みの値を渡すこと（ゲインと release は MouthTracker が済ませている）。
                //   Speaking が false のときに 0 へ倒すのは FacePolicy.Target 側の契約
                mouth: mouth,
                blink: blink,
                now: now,
                speechEndedAt: _faceLatch.SpeechEndedAt);

            // ★ Time.deltaTime ではなく Time.unscaledDeltaTime（UpdateGaze と同じ理由。
            //   位相を realtime で回している以上、緩和も同じ時間軸でないと timeScale = 0 で飛ぶ）
            _faceWeights = FacePolicy.Evaluate(input, _faceWeights, Time.unscaledDeltaTime, FaceParamsFromInspector());

            // ★★ FaceWeights → ExpressionKey の対応表はここ1箇所だけ。他へ書き写さないこと
            _faceBuffer[HappyKey] = _faceWeights.Happy;
            _faceBuffer[AngryKey] = _faceWeights.Angry;
            _faceBuffer[SadKey] = _faceWeights.Sad;
            _faceBuffer[RelaxedKey] = _faceWeights.Relaxed;
            _faceBuffer[SurprisedKey] = _faceWeights.Surprised;
            _faceBuffer[NeutralKey] = _faceWeights.Neutral;
            _faceBuffer[AaKey] = _faceWeights.Aa;
            _faceBuffer[BlinkKey] = _faceWeights.Blink;

            // ★ SetWeights ではなく SetWeightsNonAlloc（_faceBuffer の doc を参照）。
            //   モデルに無い preset は黙って無視される（例外もエラーも出ない）ので、
            //   「どれを持っているか」は OnLoaded の LogExpressionDiagnostics で1回出してある
            expression.SetWeightsNonAlloc(_faceBuffer);

            if (faceDebugLog) LogFace(now, expression);
        }

        private FaceParams FaceParamsFromInspector()
        {
            return new FaceParams(
                expressionLerpSeconds,
                faceHoldSeconds,
                promptSurpriseWeight,
                blinkSuppressAboveHappy,
                useNeutralExpression,
                mouthScaleHappy,
                mouthScaleSad);
        }

        /// <summary>
        /// 読み込んだモデルが表情に関して何を持っているかを1回だけ出す。
        ///
        /// ★ <b><c>SetWeight</c> はモデルに無い preset を黙って無視する</b>
        ///   （<c>Vrm10RuntimeExpression</c> が <c>_inputWeights.ContainsKey(key)</c> で弾く）。
        ///   「表情が変わらない」が<b>例外もエラーも無しに</b>起きるので、持っている preset を
        ///   起動時に必ず記録しておく。
        /// </summary>
        private void LogExpressionDiagnostics()
        {
            var runtime = _instance.Runtime;
            var expression = runtime != null ? runtime.Expression : null;
            if (expression == null)
            {
                Debug.LogWarning("[Mascot] Runtime.Expression が取れないので、表情は動きません");
                return;
            }

            var keys = expression.ExpressionKeys;
            var names = new List<string>(keys.Count);
            foreach (var key in keys) names.Add(key.Name);
            names.Sort(StringComparer.Ordinal);

            Debug.Log($"[Mascot] expression: {string.Join(", ", names)}（{keys.Count} 件）");
            Debug.Log("[Mascot] 使う preset: " +
                      $"happy={Mark(HappyKey)} angry={Mark(AngryKey)} sad={Mark(SadKey)} " +
                      $"relaxed={Mark(RelaxedKey)} surprised={Mark(SurprisedKey)} " +
                      $"neutral={Mark(NeutralKey)} blink={Mark(BlinkKey)} aa={Mark(AaKey)}" +
                      "（○=動く / 空=枠はあるが中身が無い / ×=無い）");

            WarnAboutOverrides();
        }

        /// <summary>
        /// その preset が<b>実際に顔を動かせるか</b>。
        ///
        /// ★ <b>「キーがあるか」だけを見ないこと。</b> UniVRM の importer は、モデルが
        ///   宣言していない preset にも<b>中身が空のクリップを作る</b> —— 同梱 <c>vita.vrm</c> は
        ///   glTF に preset が 14 個しか無いのに <c>Clips</c> は 18 個で、
        ///   <c>lookUp</c> / <c>lookDown</c> / <c>lookLeft</c> / <c>lookRight</c> が
        ///   bind ゼロで生えている（<c>VrmProbe</c> の出力で確認できる）。
        ///   つまり <c>SetWeight</c> は通るのに顔は動かない、という<b>「動いて見える死体」</b>が
        ///   起こりうる。bind の数まで見て初めて診断として意味を持つ。
        /// </summary>
        private string Mark(ExpressionKey key)
        {
            var asset = _instance.Vrm != null ? _instance.Vrm.Expression : null;
            if (asset == null) return "?";

            foreach (var pair in asset.Clips)
            {
                var clip = pair.Clip;
                if (clip == null) continue;
                if (!asset.CreateKey(clip).Equals(key)) continue;

                return HasBindings(clip) ? "○" : "空";
            }

            return "×";
        }

        /// <summary>
        /// そのクリップが何かを動かせるか（bind が1つでもあるか）。
        ///
        /// ★ <b><c>NodeTransformBindings</c> を数え落とさないこと。</b>
        ///   <c>VRM10Expression</c> の bind 配列は<b>4本</b>あり
        ///   （<c>MorphTargetBindings</c> / <c>MaterialColorBindings</c> /
        ///   <c>MaterialUVBindings</c> / <c>NodeTransformBindings</c>）、
        ///   最後のひとつは <c>NodeTransformBindingMerger</c> が実際に適用している。
        ///   数え落とすと、<b>眉や耳や尻尾をボーンで動かすモデルの効いている表情を「空」と誤報する</b>
        ///   —— この診断は「顔が動かないのが正常」と「壊れて動かない」を区別するために
        ///   あるのだから、<b>作られた目的そのものの場面で誤誘導する</b>ことになる。
        ///
        /// ★ <b><c>public static</c>。<c>VrmProbe</c> はこれを呼ぶこと（書き写さない）。</b>
        ///   <c>VrmStage.MeasureBounds</c> を <c>public static</c> にしてあるのと同じ理由で、
        ///   独立実装が2つあると<b>片方だけ直したときに黙ってズレる</b>
        ///   （実際、この関数は最初 probe 側に手写しされていて、両方が同じ抜けを持っていた）。
        ///
        /// ★ 配列は <c>null</c> になりうるので長さを直接足さない。
        /// </summary>
        public static bool HasBindings(VRM10Expression clip)
        {
            if (clip == null) return false;

            return (clip.MorphTargetBindings != null && clip.MorphTargetBindings.Length > 0)
                   || (clip.MaterialColorBindings != null && clip.MaterialColorBindings.Length > 0)
                   || (clip.MaterialUVBindings != null && clip.MaterialUVBindings.Length > 0)
                   || (clip.NodeTransformBindings != null && clip.NodeTransformBindings.Length > 0);
        }

        /// <summary>
        /// 表情が瞬き / 口 / 視線をブロックするモデルかを1回だけ警告する。
        ///
        /// ★ <b>判定は「モデルの静的な定義」（<c>Vrm.Expression.Clips</c>）で行うこと。</b>
        ///   ランタイムの <c>BlinkOverrideRate</c> / <c>MouthOverrideRate</c> /
        ///   <c>LookAtOverrideRate</c> を見る手もあるが、2つの理由で異常検知にならない:
        ///   <list type="number">
        ///     <item>あれは<b>いま立てている weight に依存する動的な値</b>で、実運用では
        ///       <c>neutral</c> が支配的（<c>ruleBasedEmotionClassifier</c> がコード説明文を
        ///       <c>neutral</c> に倒すよう明示的にチューニングされている）＝ほとんどの時間 0 のまま</item>
        ///     <item>更新されるのは <c>Vrm10Runtime.Process()</c>（実行順 11000）の<b>中</b>なので、
        ///       実行順 0 のここから読むと<b>前フレームの値</b>になる</item>
        ///   </list>
        ///   静的な <c>OverrideBlink</c> / <c>OverrideMouth</c> / <c>OverrideLookAt</c> は
        ///   読み込み直後から確定していて weight に依存しない。
        /// ★ <b><c>OverrideLookAt</c> も見ること。</b> ここが <c>none</c> でないモデルでは、
        ///   表情を出した瞬間に #59 のカーソル追従（<c>LookAtEyeDirection</c>）が減衰する。
        /// ★ 自分自身のチャンネルは数えない（<c>DefaultExpressionValidator.Validate</c> と同じ扱い）。
        ///
        /// ★★ <b>走査は <see cref="DrivenKeys"/> に絞ること。</b> override 率は
        ///   <c>GetOverrideRate(clip.Override*, weight)</c> ＝ weight 依存なので、
        ///   <b>このアプリが一度も weight を立てないクリップは何もブロックできない</b>。
        ///   全クリップを見ると、<c>ih</c> / <c>ou</c> / <c>ee</c> / <c>oh</c>（使わない口の preset）や
        ///   カスタムクリップに <c>overrideBlink</c> が付いているだけで
        ///   <b>成立しえない条件の警告を毎起動・永久に出す</b>ことになる。
        ///   「読み飛ばす癖がつくぶん有害」（docs/mascot.md）そのもの。
        /// ★ <b>絞る対象はキーの集合であって、見る項目ではない。</b>
        ///   <c>OverrideLookAt</c> を見る判断（#59 のカーソル追従が殺されるケース）は維持する。
        /// </summary>
        private void WarnAboutOverrides()
        {
            var asset = _instance.Vrm != null ? _instance.Vrm.Expression : null;
            if (asset == null) return;

            var blocksMouth = false;
            var blocksBlink = false;
            var blocksLookAt = false;

            foreach (var pair in asset.Clips)
            {
                var clip = pair.Clip;
                if (clip == null) continue;

                var key = asset.CreateKey(clip);
                if (!IsDriven(key)) continue;

                if (!key.IsMouth && clip.OverrideMouth != ExpressionOverrideType.none) blocksMouth = true;
                if (!key.IsBlink && clip.OverrideBlink != ExpressionOverrideType.none) blocksBlink = true;
                if (!key.IsLookAt && clip.OverrideLookAt != ExpressionOverrideType.none) blocksLookAt = true;
            }

            if (blocksMouth) Debug.LogWarning("[Mascot] このモデルは表情が口をブロックします。喋っても口が動かないことがあります");
            if (blocksBlink) Debug.LogWarning("[Mascot] このモデルは表情が瞬きをブロックします。表情が強いと瞬かないことがあります");
            if (blocksLookAt) Debug.LogWarning("[Mascot] このモデルは表情が視線をブロックします。カーソル追従が効かないことがあります");
        }

        /// <summary>このアプリが weight を書く preset か。</summary>
        private static bool IsDriven(ExpressionKey key)
        {
            for (var i = 0; i < DrivenKeys.Length; i++)
            {
                if (DrivenKeys[i].Equals(key)) return true;
            }
            return false;
        }

        /// <summary>
        /// 今の emotion / kind と<b>実効</b> weight を1秒ごとに出す。
        ///
        /// ★ <b>実運用では <c>neutral</c> が支配的なので、「顔が動かないのが正常」と
        ///   「壊れて動かない」の区別が目で見てもつかない。</b> しかも #59 でアイドルと視線が
        ///   動いているぶん、体が動いているのを見て「動いているから大丈夫」と流しやすい。
        ///   このログは<b>顔だけが死んでいても気付けるようにする</b>ためにある。
        /// ★ <b>「実効」は <c>ActualWeights</c> から読むこと。</b> 自前で計算した
        ///   <c>_faceWeights</c> をそのまま出しても、<c>SetWeight</c> がモデルに無い preset を
        ///   黙って無視したケースを検出できない（両方出しているのはそのため）。
        /// ★ <b>ただし「実効」は<c>1フレーム古い</c>。</b> <c>ActualWeights</c> を埋めるのは
        ///   <c>Vrm10RuntimeExpression.Apply</c> ＝ <c>Vrm10Runtime.Process()</c>（実行順 11000）の
        ///   中で、実行順 0 のここはその<b>手前</b>だから。<b>切り替わりの最中に目標と実効が
        ///   1フレームぶんずれて見えるのは正常</b>で、バグではない —— 落ち着いたあとの値が
        ///   食い違っていたら、そのときが本物（モデルが preset を持っていないか、override で
        ///   減衰されている）。
        /// ★ <b>同じ文面を間引かないこと。</b> <c>neutral</c> のまま動かないのが正常なので、
        ///   重複を抑えると<b>「正常」のときだけ何も出なくなり、目的と正反対</b>になる。
        ///   時間で間引くだけにする。
        /// </summary>
        private void LogFace(double now, Vrm10RuntimeExpression expression)
        {
            if (now - _faceLoggedAt < _faceLogIntervalSeconds) return;
            _faceLoggedAt = now;

            var actual = expression.ActualWeights;
            Debug.Log($"[Mascot] face: kind={_faceLatch.Kind} emotion={_faceLatch.Emotion} speaking={Speaking}" +
                      $" | 目標 happy={_faceWeights.Happy:F2} angry={_faceWeights.Angry:F2} sad={_faceWeights.Sad:F2}" +
                      $" relaxed={_faceWeights.Relaxed:F2} surprised={_faceWeights.Surprised:F2}" +
                      $" neutral={_faceWeights.Neutral:F2} aa={_faceWeights.Aa:F2} blink={_faceWeights.Blink:F2}" +
                      $" | 実効 happy={Actual(actual, HappyKey):F2} angry={Actual(actual, AngryKey):F2}" +
                      $" sad={Actual(actual, SadKey):F2} relaxed={Actual(actual, RelaxedKey):F2}" +
                      $" surprised={Actual(actual, SurprisedKey):F2} neutral={Actual(actual, NeutralKey):F2}" +
                      $" aa={Actual(actual, AaKey):F2} blink={Actual(actual, BlinkKey):F2}");
        }

        /// <summary>モデルが持たない preset は <c>ActualWeights</c> に載らないので 0 を返す。</summary>
        private static float Actual(IReadOnlyDictionary<ExpressionKey, float> weights, ExpressionKey key)
        {
            return weights != null && weights.TryGetValue(key, out var weight) ? weight : 0f;
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
