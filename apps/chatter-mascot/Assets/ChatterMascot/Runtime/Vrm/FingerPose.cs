using System.Collections.Generic;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// VRMA に無い指ボーンの既定姿勢（軽く丸めた、握っていない手）。<b>純粋関数。</b>
    ///
    /// ★ #88。同梱 <c>idle_loop.vrma</c> を含む 22 ボーンの Humanoid クリップは指を持たない。
    ///   <c>Vrm10Retarget.Retarget</c> はモデル側（30 指ボーンを含む）のボーンを走査して
    ///   VRMA 側の <c>INormalizedPoseProvider.GetNormalizedLocalRotation</c> を呼ぶので、
    ///   ファイルに無いボーンにも毎フレーム値を要求される。UniVRM の既定実装
    ///   （<c>InitRotationPoseProvider</c>）はそこで <c>Quaternion.identity</c> を返し、
    ///   正規化空間での identity は VRM 1.0 の T ポーズ＝指がまっすぐ伸びた状態になる。
    ///   このクラスはその「まっすぐ」を「軽く丸めた」に差し替えるための<b>値</b>だけを持つ
    ///   （差し替える仕組み側は <see cref="FingerFallbackPoseProvider"/>）。
    /// </summary>
    public static class FingerPose
    {
        /// <summary>人差し指〜小指、第1関節（付け根）の丸め角（度）</summary>
        public const float ProximalDegrees = 18f;

        /// <summary>人差し指〜小指、第2関節の丸め角（度）</summary>
        public const float IntermediateDegrees = 22f;

        /// <summary>人差し指〜小指、第3関節（指先）の丸め角（度）</summary>
        public const float DistalDegrees = 15f;

        /// <summary>
        /// 親指、第1関節の丸め角（度）。
        ///
        /// ★ <b>いまは 0（曲げない）。</b> 親指は他の4指と可動軸の向きが違い、他指と同じ
        ///   Z 軸まわりの回転をそのまま当てはめると不自然な丸まり方になりかねない。
        ///   実機のスクリーンショットで軸を確定できるまでは「伸びたまま」にとどめ、
        ///   少なくとも「明後日の方向に折れ曲がる」誤りだけは避ける。有効化は #88 の
        ///   後続（実機確認を経た第2段）に委ねる。定数として残すのは、その第2段が
        ///   ここを書き換えるだけで済むようにするため。
        /// </summary>
        public const float ThumbProximalDegrees = 0f;

        /// <summary>親指、第2関節の丸め角（度）。<see cref="ThumbProximalDegrees"/> と同じ理由でいまは 0</summary>
        public const float ThumbIntermediateDegrees = 0f;

        /// <summary>親指、第3関節の丸め角（度）。<see cref="ThumbProximalDegrees"/> と同じ理由でいまは 0</summary>
        public const float ThumbDistalDegrees = 0f;

        /// <summary>
        /// 指の揺れ（<see cref="RelaxedCurl(HumanBodyBones, double)"/>）の振幅（度）。
        /// 基準の丸め角に対して ± この角度だけ振動する。
        /// </summary>
        public const float SwayDegrees = 2.5f;

        /// <summary>
        /// 指の揺れの周期（秒）。
        ///
        /// ★ <c>IdleParams.Default.breathSeconds</c>（<c>Runtime/Vrm/IdlePose.cs:104-105</c>、
        ///   既定 4秒）と同じ値にして、体の呼吸と「同じ呼吸で揺れている」ように読ませてある。
        ///   <b>ただし意図して独立した定数にしてある。</b> 同梱 <c>idle_loop.vrma</c> の
        ///   待機モーションは外部データで、その内部の周期を読み取る手段がこちら側に無い
        ///   （VRMA 版のフォールバック＝<see cref="FingerFallbackPoseProvider"/> は VRMA の
        ///   本体アニメーションと共存するので、指の揺れは VRMA の呼吸とは別物として動く）。
        ///   合わせられるのは「桁が近い」ところまでで、<b>位相を合わせる（呼吸と指の揺れが
        ///   必ず同じタイミングで動く）ことは目標にしていない。</b>
        /// </summary>
        public const float SwaySeconds = 4f;

        /// <summary>
        /// 指ごとに足す揺れの位相ずれ（ラジアン）。人差し指から小指へ向けて
        /// <c>fingerIndex</c>（0〜3）倍で足す——4本の指が振り子のように同じタイミングで
        /// 動く「一枚板」に見えないようにするための、ごく小さな時間差。
        /// </summary>
        public const float SwayLagPerFingerRadians = 0.5f;

        /// <summary>
        /// 1ボーンぶんの静的なデータ。<see cref="RelaxedCurl(HumanBodyBones)"/>（基準の丸め）と
        /// <see cref="RelaxedCurl(HumanBodyBones, double)"/>（揺れ）の両方をここから引く
        /// （対応表を2箇所に持たないため）。
        /// </summary>
        private readonly struct BoneInfo
        {
            /// <summary>基準の丸め角（度）。符号は付かない大きさだけ——左右の符号は <see cref="Sign"/> が持つ</summary>
            public readonly float BaseDegrees;

            /// <summary>人差し指 0・中指 1・薬指 2・小指 3。親指は 0（揺れないので値そのものは無関係）</summary>
            public readonly int FingerIndex;

            /// <summary>左が +1、右が -1。<see cref="RelaxedCurl(HumanBodyBones)"/> の「右手が負・左手が正」の約束そのもの</summary>
            public readonly float Sign;

            public BoneInfo(float baseDegrees, int fingerIndex, float sign)
            {
                BaseDegrees = baseDegrees;
                FingerIndex = fingerIndex;
                Sign = sign;
            }
        }

        /// <summary>
        /// 指1本ぶんの左右ボーンと丸め角、指番号。<see cref="_fingerBones"/> と <see cref="_boneInfo"/> の
        /// 両方をここから組み立てる（列挙と角度の対応表を2箇所に持たないため）。
        /// </summary>
        private static readonly (HumanBodyBones Left, HumanBodyBones Right, float Degrees, int FingerIndex)[] _segments =
        {
            (HumanBodyBones.LeftThumbProximal, HumanBodyBones.RightThumbProximal, ThumbProximalDegrees, 0),
            (HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.RightThumbIntermediate, ThumbIntermediateDegrees, 0),
            (HumanBodyBones.LeftThumbDistal, HumanBodyBones.RightThumbDistal, ThumbDistalDegrees, 0),

            (HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal, ProximalDegrees, 0),
            (HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.RightIndexIntermediate, IntermediateDegrees, 0),
            (HumanBodyBones.LeftIndexDistal, HumanBodyBones.RightIndexDistal, DistalDegrees, 0),

            (HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal, ProximalDegrees, 1),
            (HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate, IntermediateDegrees, 1),
            (HumanBodyBones.LeftMiddleDistal, HumanBodyBones.RightMiddleDistal, DistalDegrees, 1),

            (HumanBodyBones.LeftRingProximal, HumanBodyBones.RightRingProximal, ProximalDegrees, 2),
            (HumanBodyBones.LeftRingIntermediate, HumanBodyBones.RightRingIntermediate, IntermediateDegrees, 2),
            (HumanBodyBones.LeftRingDistal, HumanBodyBones.RightRingDistal, DistalDegrees, 2),

            (HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal, ProximalDegrees, 3),
            (HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.RightLittleIntermediate, IntermediateDegrees, 3),
            (HumanBodyBones.LeftLittleDistal, HumanBodyBones.RightLittleDistal, DistalDegrees, 3),
        };

        private static readonly HumanBodyBones[] _fingerBones = BuildFingerBones();
        private static readonly Dictionary<HumanBodyBones, BoneInfo> _boneInfo = BuildBoneInfo();

        /// <summary>指ボーン30本の全列挙（親指〜小指 × Proximal/Intermediate/Distal × 左右）</summary>
        public static IEnumerable<HumanBodyBones> FingerBones => _fingerBones;

        /// <summary>指ボーン（30本のいずれか）かどうか</summary>
        public static bool IsFinger(HumanBodyBones bone) => _boneInfo.ContainsKey(bone);

        /// <summary>
        /// 正規化ローカル空間での「軽く丸めた」指の姿勢（揺れ無し・基準そのもの）。
        /// 指以外のボーンは <c>Quaternion.identity</c>。
        ///
        /// ★ Z 軸まわりの回転。<b>符号は右手が負・左手が正</b>——<c>IdlePose.Evaluate</c> の腕
        ///   （<c>RestUpperArmDegrees</c> / <c>RestLowerArmDegrees</c>）と同じ約束にそろえてある。
        ///   ControlRig の正規化 T ポーズは左右対称に開いているので、この符号も左右対称に反転する。
        ///   <b>符号は実機のスクリーンショットで決める。導出で書き換えないこと。
        ///   逆に反っていたら軸を変える前にまず符号を反転する。</b>
        /// ★ <see cref="RelaxedCurl(HumanBodyBones, double)"/>（揺れあり）は、<b>この基準を
        ///   中心に</b> sin波で振動する——<c>sin</c> が 0 になる位相での <see cref="RelaxedCurl(HumanBodyBones, double)"/>
        ///   と一致する、というだけの関係で、こちらは <c>now</c> を持たない分岐として別に定義する
        ///   （揺れの位相計算を経由しない、素の値をいつでも引けるようにするため）。
        /// </summary>
        public static Quaternion RelaxedCurl(HumanBodyBones bone)
        {
            if (!_boneInfo.TryGetValue(bone, out var info)) return Quaternion.identity;
            return info.BaseDegrees == 0f ? Quaternion.identity : Quaternion.Euler(0f, 0f, info.Sign * info.BaseDegrees);
        }

        /// <summary>
        /// 正規化ローカル空間での「揺れる、軽く丸めた」指の姿勢。<see cref="RelaxedCurl(HumanBodyBones)"/>
        /// （基準の丸め）を中心に、<paramref name="now"/> の経過で ± <see cref="SwayDegrees"/> だけ振動する。
        ///
        /// ★ <b>基準角が 0（いまは親指）のボーンは揺れない。</b> 揺れは「既にある丸めを揺らす」
        ///   ことが前提で、揺れそのものが丸めを作ってはいけない——親指を有効化する #88 の
        ///   後続で、<see cref="ThumbProximalDegrees"/> 等を書き換えるだけで自動的に揺れ始める。
        /// ★ 位相は <see cref="Oscillator.Phase"/> を使うこと。<c>now</c> は
        ///   <c>Time.realtimeSinceStartupAsDouble</c>（常駐アプリなので日単位まで伸びる）で、
        ///   float へ落とす前に周期で畳む必要がある——直接 <c>(float)(2π·now/period)</c> にすると
        ///   <see cref="Oscillator.Phase"/> の doc にある「7日でカクつく／止まって見える」不具合を
        ///   ここでも踏む。
        /// ★ 指ごとに <see cref="SwayLagPerFingerRadians"/> × <c>FingerIndex</c>
        ///   （人差し指 0・中指 1・薬指 2・小指 3）だけ位相をずらし、4本が同じタイミングで
        ///   動く「一枚板」に見えないようにする。
        /// ★ 左右は<b>同位相・同符号の約束</b>でミラーする（<see cref="RelaxedCurl(HumanBodyBones)"/> の
        ///   符号の約束そのものを揺れにも掛けるだけ）——揺れを左右で逆位相にすると、
        ///   片方が閉じる瞬間にもう片方が開く「手が別々の生き物」に見える。
        /// ★ <see cref="FingerFallbackPoseProvider"/> の doc のとおり、これは
        ///   <c>Vrm10Retarget.Retarget</c> から1フレームに最大30回（指ボーン30本）呼ばれる。
        ///   <see cref="Oscillator.Phase"/> + <c>Mathf.Sin</c> 1回のみで、
        ///   <b>ここでアロケーションを増やさないこと。</b>
        /// </summary>
        public static Quaternion RelaxedCurl(HumanBodyBones bone, double now)
        {
            if (!_boneInfo.TryGetValue(bone, out var info)) return Quaternion.identity;
            if (info.BaseDegrees == 0f) return Quaternion.identity;

            var lag = info.FingerIndex * SwayLagPerFingerRadians;
            var sway = SwayDegrees * Mathf.Sin(Oscillator.Phase(now, SwaySeconds) + lag);
            var degrees = info.BaseDegrees + sway;
            return Quaternion.Euler(0f, 0f, info.Sign * degrees);
        }

        private static HumanBodyBones[] BuildFingerBones()
        {
            var bones = new HumanBodyBones[_segments.Length * 2];
            for (var i = 0; i < _segments.Length; i++)
            {
                bones[i * 2] = _segments[i].Left;
                bones[i * 2 + 1] = _segments[i].Right;
            }
            return bones;
        }

        private static Dictionary<HumanBodyBones, BoneInfo> BuildBoneInfo()
        {
            var map = new Dictionary<HumanBodyBones, BoneInfo>(_segments.Length * 2);
            foreach (var (left, right, degrees, fingerIndex) in _segments)
            {
                map[left] = new BoneInfo(degrees, fingerIndex, 1f);
                map[right] = new BoneInfo(degrees, fingerIndex, -1f);
            }
            return map;
        }
    }
}
