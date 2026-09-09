using System;
using System.Collections.Generic;
using System.Globalization;
using UniGLTF.Utils;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 2つの <see cref="IVrm10Animation"/> を <c>ControlRig</c> のレベルで混ぜる、時間で進む
    /// 合成 <see cref="IVrm10Animation"/>（#70）。<b><c>MonoBehaviour</c> ではない。</b>
    /// 何も所有しない——<c>from</c> / <c>to</c> の破棄は呼び出し側（<see cref="VrmMotionPlayer"/>）の責任。
    ///
    /// ★★ <b>なぜここで実装するか。</b> <c>Vrm10Runtime.Process()</c> は
    ///   <c>VrmAnimation</c> のスロットを<b>1つ</b>しか持たない（<c>Vrm10Runtime.cs:105-126</c>）。
    ///   2本のモーションをクロスフェードするには「混ぜた結果」をこのスロット1つに差す必要があり、
    ///   それがこのクラスの仕事。雛形は <c>Vrm10TPose</c>（同じく <c>MonoBehaviour</c> でない
    ///   <c>IVrm10Animation</c> の実装。VRM10 パッケージ同梱）。
    ///
    /// ★ <b>判断とブレンドの数式そのものは <see cref="CrossFade"/>（<c>Runtime/Vrm/</c>）にある。</b>
    ///   ここは VRM10 の型（<see cref="INormalizedPoseProvider"/> / <see cref="ITPoseProvider"/>）に
    ///   依存する「混ぜ方の配線」だけを持つ——<c>ChatterMascot.Tests.asmdef</c> は
    ///   <c>ChatterMascot.Runtime</c> しか参照しないので、判断側だけを EditMode テストで固定できる。
    /// </summary>
    internal sealed class CrossFadeAnimation : IVrm10Animation, INormalizedPoseProvider
    {
        /// <summary>
        /// ★★ <b>static readonly の空 Dictionary を1つだけ持つ。</b> <c>Vrm10Runtime.Process()</c> は
        ///   <c>ExpressionMap</c> を<b>毎フレーム</b> foreach する（<c>Vrm10Runtime.cs:116</c>）。
        ///   感情モーションが鳴るたびに空の Dictionary を new すると、そのぶん常駐アプリの
        ///   GC 予算を削る——中身が空で列挙するものが無いなら、確保自体が無駄。
        /// </summary>
        private static readonly IReadOnlyDictionary<ExpressionKey, Func<float>> EmptyExpressionMap =
            new Dictionary<ExpressionKey, Func<float>>();

        /// <summary>
        /// 合成 <see cref="ITPoseProvider"/>。<b>Hips だけ答える。</b>
        ///
        /// ★★ <b>なぜ Hips だけでよいか。</b> <c>Vrm10Retarget.Retarget</c> は<b>モデル側
        ///   （sink）の TPose</b> で全ボーンを回し（<c>ITPoseProviderExtensions.EnumerateBoneParentPairs</c>）、
        ///   <c>source.TPose</c>（＝ここ）は hips のスケーリングにしか使わない
        ///   （<c>Vrm10Retarget.cs:17-18</c>、<c>source.TPose.GetWorldTransform(Hips)</c> だけを読む）。
        ///   他のボーンを聞かれることが無いので、Hips 以外は <c>null</c> のままでよい。
        /// ★★ <b><c>null</c> を返してはいけないのは Hips だけ。</b> <c>Retarget</c> は
        ///   <c>source.TPose.GetWorldTransform(Hips).Value</c> と<b>ノーガードで <c>.Value</c> を読む</b>
        ///   ので、Hips で <c>null</c> を返すと <c>Retarget</c> 自体が <c>InvalidOperationException</c>
        ///   （Nullable の <c>.Value</c>）で落ちる。<c>Vrm10TPose.Skeleton</c> と同じ雛形。
        /// ★ <b>高さは <c>1</c>（<c>Vector3.up</c>）に固定。</b> 単位は無次元
        ///   （<see cref="CrossFade.NormalizeHipsDelta"/> の doc 参照）——このクラス自身が
        ///   「T ポーズの hips の高さは 1」と申告し、<c>Vrm10Retarget</c> が
        ///   <c>sink.TPose.Hips.y / 1</c> でモデルの背丈へ掛け戻す。
        /// </summary>
        private sealed class HipsOnlyTPose : ITPoseProvider
        {
            public static readonly HipsOnlyTPose Instance = new HipsOnlyTPose();

            private HipsOnlyTPose() { }

            public EuclideanTransform? GetWorldTransform(HumanBodyBones bone)
            {
                return bone == HumanBodyBones.Hips
                    ? new EuclideanTransform(Vector3.up)
                    : (EuclideanTransform?)null;
            }
        }

        private readonly INormalizedPoseProvider _fromPose;
        private readonly ITPoseProvider _fromTPose;
        private readonly INormalizedPoseProvider _toPose;
        private readonly ITPoseProvider _toTPose;
        private readonly double _startedAt;
        private readonly float _fadeSeconds;

        private float _t;

        /// <param name="from">
        /// フェード元の <c>ControlRig</c>。
        /// ★★ <b>呼び出し側が既に <c>FingerFallbackPoseProvider.Wrap</c> を掛けた後のものを渡すこと。</b>
        ///   ここでは掛けない——「ファイルに指ボーンが無ければ既定の丸めで補う」は1本の VRMA を
        ///   対象にした判定（<c>ITPoseProvider.GetWorldTransform</c> の <c>HasValue</c>）で、
        ///   2本を混ぜるこのクラスに持ち込むと「どちらの TPose を基準に判定するか」という
        ///   要らない問いが増える。<c>VrmMotionPlayer</c> が両方に <c>Wrap</c> 済みのものを渡す契約。
        /// </param>
        /// <param name="to">フェード先の <c>ControlRig</c>。<paramref name="from"/> と同じ契約。</param>
        /// <param name="startedAt">フェード開始時刻（<c>Time.realtimeSinceStartupAsDouble</c>）。</param>
        /// <param name="fadeSeconds">フェードにかける秒数。<see cref="CrossFade.Progress"/> に渡す。</param>
        public CrossFadeAnimation(
            (INormalizedPoseProvider Pose, ITPoseProvider TPose) from,
            (INormalizedPoseProvider Pose, ITPoseProvider TPose) to,
            double startedAt,
            float fadeSeconds)
        {
            _fromPose = from.Pose;
            _fromTPose = from.TPose;
            _toPose = to.Pose;
            _toTPose = to.TPose;
            _startedAt = startedAt;
            _fadeSeconds = fadeSeconds;
        }

        /// <summary>
        /// 進捗を進める。<b>時計は引数で受け取る</b>——このクラス自身は <c>Time.*</c> を直接読まない
        /// （呼び出し側の <see cref="VrmMotionPlayer"/> が <c>VrmCharacter.LateUpdate</c> から渡す
        /// <c>now</c> を素通しする）。
        /// </summary>
        public void Tick(double now)
        {
            _t = CrossFade.Ease(CrossFade.Progress(_startedAt, now, _fadeSeconds));
        }

        /// <summary>フェードが終わったか（<c>t &gt;= 1</c>）。</summary>
        public bool IsDone => _t >= 1f;

        /// <summary>
        /// <c>(this の provider, Hips だけ答える合成 TPose)</c>。<c>this</c> を provider として
        /// 返すのは、<see cref="GetNormalizedLocalRotation"/> / <see cref="GetRawHipsPosition"/>
        /// をこのクラス自身が実装しているため。
        /// </summary>
        public (INormalizedPoseProvider, ITPoseProvider) ControlRig => (this, HipsOnlyTPose.Instance);

        public IReadOnlyDictionary<ExpressionKey, Func<float>> ExpressionMap => EmptyExpressionMap;

        /// <summary>
        /// ★ <c>null</c> 固定。<c>Vrm10Runtime.Process()</c> は <c>VrmAnimation.LookAt.HasValue</c>
        ///   のときだけ <c>LookAt.LookAtInput</c> を上書きする——<c>null</c> なら触らないので、
        ///   フェード中も <see cref="VrmCharacter"/> 側のカーソル追従の視線がそのまま生きる。
        /// </summary>
        public LookAtInput? LookAt => null;

        public void ShowBoxMan(bool enable) { }
        public void SetBoxManMaterial(Material material) { }

        /// <summary>★ 何も所有していない（<c>from</c> / <c>to</c> の破棄は呼び出し側の責任）ので no-op。</summary>
        public void Dispose() { }

        public Quaternion GetNormalizedLocalRotation(HumanBodyBones bone, HumanBodyBones parentBone)
        {
            return CrossFade.BlendRotation(
                _fromPose.GetNormalizedLocalRotation(bone, parentBone),
                _toPose.GetNormalizedLocalRotation(bone, parentBone),
                _t);
        }

        /// <summary>
        /// ★★ <b>生の hips 位置を直接 Lerp してはいけない。</b> 同梱 <c>idle_loop.vrma</c> は
        ///   cm スケール（hips y≈90）で書き出されている一方、VRoid の書き出しは m スケール
        ///   （hips y≈0.98）——単位が実測で約100倍違う。素朴に Lerp すると、フェードの途中で
        ///   腰が瞬間的に大きく飛ぶ（cm 側の delta が m 側よりケタで大きいため）。
        ///   <see cref="CrossFade.NormalizeHipsDelta"/> で T ポーズの高さを使って無次元化してから
        ///   混ぜ（<see cref="CrossFade.BlendHips"/>）、ここで自分の T ポーズの高さ
        ///   （<see cref="HipsOnlyTPose"/> ＝ <c>Vector3.up</c>、高さ 1）に足し戻す。
        ///   <c>Vrm10Retarget</c> がそこから <c>sink.TPose.Hips.y / 1</c> でモデルの背丈に戻す。
        /// </summary>
        public Vector3 GetRawHipsPosition()
        {
            var fromDelta = NormalizedDelta(_fromPose, _fromTPose);
            var toDelta = NormalizedDelta(_toPose, _toTPose);
            return Vector3.up + CrossFade.BlendHips(fromDelta, toDelta, _t);
        }

        /// <summary>
        /// ★ <paramref name="tpose"/> が Hips を持たない（<c>GetWorldTransform</c> が <c>null</c>）
        ///   ときは差分 0 とする。理論上は起きない——このクラスに渡る <c>from</c> / <c>to</c> は
        ///   常に <c>Vrm10AnimationInstance.ControlRig</c>（Hips を必ず持つ）か、この
        ///   <see cref="CrossFadeAnimation"/> 自身の <see cref="ControlRig"/>（<see cref="HipsOnlyTPose"/>
        ///   が必ず Hips を返す）のどちらかだが、既定値へ倒せる箇所は倒しておく
        ///   （<c>Retarget</c> のような <c>.Value</c> のノーガード読みをここでは避ける）。
        /// </summary>
        private static Vector3 NormalizedDelta(INormalizedPoseProvider pose, ITPoseProvider tpose)
        {
            var hips = tpose.GetWorldTransform(HumanBodyBones.Hips);
            if (!hips.HasValue) return Vector3.zero;
            return CrossFade.NormalizeHipsDelta(pose.GetRawHipsPosition(), hips.Value.Translation);
        }

        /// <summary>
        /// フェードの出力に NaN が混ざっていないかを診断する（#103）。<b>最初に見つかった1箇所だけ</b>
        /// を <paramref name="description"/> に入れて <c>true</c> を返す。無ければ <c>false</c>。
        ///
        /// ★ 調べる順は「混ぜる前の入力（<c>from</c> / <c>to</c>）」→「混ぜた結果（<c>blend</c>）」。
        ///   入力側で既に NaN なら、ブレンドの数式（<see cref="CrossFade"/>）を疑う理由が無い。
        /// </summary>
        public bool TryDescribeNaN(out string description)
        {
            var body = FindNaNBody();
            if (body == null)
            {
                description = null;
                return false;
            }

            description = body + " t=" + _t.ToString("F3", CultureInfo.InvariantCulture);
            return true;
        }

        private string FindNaNBody()
        {
            var fromTPoseHips = _fromTPose.GetWorldTransform(HumanBodyBones.Hips)?.Translation;
            if (fromTPoseHips.HasValue && HasNaN(fromTPoseHips.Value)) return Describe("from.tposeHips", fromTPoseHips.Value);

            var fromRawHips = _fromPose.GetRawHipsPosition();
            if (HasNaN(fromRawHips)) return Describe("from.rawHips", fromRawHips);

            var toTPoseHips = _toTPose.GetWorldTransform(HumanBodyBones.Hips)?.Translation;
            if (toTPoseHips.HasValue && HasNaN(toTPoseHips.Value)) return Describe("to.tposeHips", toTPoseHips.Value);

            var toRawHips = _toPose.GetRawHipsPosition();
            if (HasNaN(toRawHips)) return Describe("to.rawHips", toRawHips);

            var blendHips = GetRawHipsPosition();
            if (HasNaN(blendHips)) return Describe("blend", blendHips);

            // ★ Retarget が Hips に渡す parentBone は常に LastBone
            //   （ITPoseProviderExtensions.EnumerateBoneParentPairs の Hips 分岐と同じ）
            var fromHipsRot = _fromPose.GetNormalizedLocalRotation(HumanBodyBones.Hips, HumanBodyBones.LastBone);
            if (HasNaN(fromHipsRot)) return Describe("from.hipsRot", fromHipsRot);

            var toHipsRot = _toPose.GetNormalizedLocalRotation(HumanBodyBones.Hips, HumanBodyBones.LastBone);
            if (HasNaN(toHipsRot)) return Describe("to.hipsRot", toHipsRot);

            var blendHipsRot = GetNormalizedLocalRotation(HumanBodyBones.Hips, HumanBodyBones.LastBone);
            if (HasNaN(blendHipsRot)) return Describe("blend.hipsRot", blendHipsRot);

            return null;
        }

        private static bool HasNaN(Vector3 v)
        {
            return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z);
        }

        private static bool HasNaN(Quaternion q)
        {
            return float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w);
        }

        private static string Describe(string label, Vector3 v)
        {
            return label + "=(" + v.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                   v.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                   v.z.ToString("F3", CultureInfo.InvariantCulture) + ")";
        }

        private static string Describe(string label, Quaternion q)
        {
            return label + "=(" + q.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
                   q.y.ToString("F3", CultureInfo.InvariantCulture) + "," +
                   q.z.ToString("F3", CultureInfo.InvariantCulture) + "," +
                   q.w.ToString("F3", CultureInfo.InvariantCulture) + ")";
        }
    }
}
