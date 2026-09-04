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
        public const float ProximalDegrees = 25f;

        /// <summary>人差し指〜小指、第2関節の丸め角（度）</summary>
        public const float IntermediateDegrees = 30f;

        /// <summary>人差し指〜小指、第3関節（指先）の丸め角（度）</summary>
        public const float DistalDegrees = 20f;

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
        /// 指1本ぶんの左右ボーンと丸め角。<see cref="_fingerBones"/> と <see cref="_relaxedCurl"/> の
        /// 両方をここから組み立てる（列挙と角度の対応表を2箇所に持たないため）。
        /// </summary>
        private static readonly (HumanBodyBones Left, HumanBodyBones Right, float Degrees)[] _segments =
        {
            (HumanBodyBones.LeftThumbProximal, HumanBodyBones.RightThumbProximal, ThumbProximalDegrees),
            (HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.RightThumbIntermediate, ThumbIntermediateDegrees),
            (HumanBodyBones.LeftThumbDistal, HumanBodyBones.RightThumbDistal, ThumbDistalDegrees),

            (HumanBodyBones.LeftIndexProximal, HumanBodyBones.RightIndexProximal, ProximalDegrees),
            (HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.RightIndexIntermediate, IntermediateDegrees),
            (HumanBodyBones.LeftIndexDistal, HumanBodyBones.RightIndexDistal, DistalDegrees),

            (HumanBodyBones.LeftMiddleProximal, HumanBodyBones.RightMiddleProximal, ProximalDegrees),
            (HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.RightMiddleIntermediate, IntermediateDegrees),
            (HumanBodyBones.LeftMiddleDistal, HumanBodyBones.RightMiddleDistal, DistalDegrees),

            (HumanBodyBones.LeftRingProximal, HumanBodyBones.RightRingProximal, ProximalDegrees),
            (HumanBodyBones.LeftRingIntermediate, HumanBodyBones.RightRingIntermediate, IntermediateDegrees),
            (HumanBodyBones.LeftRingDistal, HumanBodyBones.RightRingDistal, DistalDegrees),

            (HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightLittleProximal, ProximalDegrees),
            (HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.RightLittleIntermediate, IntermediateDegrees),
            (HumanBodyBones.LeftLittleDistal, HumanBodyBones.RightLittleDistal, DistalDegrees),
        };

        private static readonly HumanBodyBones[] _fingerBones = BuildFingerBones();
        private static readonly Dictionary<HumanBodyBones, Quaternion> _relaxedCurl = BuildRelaxedCurl();

        /// <summary>指ボーン30本の全列挙（親指〜小指 × Proximal/Intermediate/Distal × 左右）</summary>
        public static IEnumerable<HumanBodyBones> FingerBones => _fingerBones;

        /// <summary>指ボーン（30本のいずれか）かどうか</summary>
        public static bool IsFinger(HumanBodyBones bone) => _relaxedCurl.ContainsKey(bone);

        /// <summary>
        /// 正規化ローカル空間での「軽く丸めた」指の姿勢。指以外のボーンは <c>Quaternion.identity</c>。
        ///
        /// ★ Z 軸まわりの回転。<b>符号は右手が負・左手が正</b>——<c>IdlePose.Evaluate</c> の腕
        ///   （<c>RestUpperArmDegrees</c> / <c>RestLowerArmDegrees</c>）と同じ約束にそろえてある。
        ///   ControlRig の正規化 T ポーズは左右対称に開いているので、この符号も左右対称に反転する。
        ///   <b>符号は実機のスクリーンショットで決める。導出で書き換えないこと。
        ///   逆に反っていたら軸を変える前にまず符号を反転する。</b>
        /// </summary>
        public static Quaternion RelaxedCurl(HumanBodyBones bone) =>
            _relaxedCurl.TryGetValue(bone, out var q) ? q : Quaternion.identity;

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

        private static Dictionary<HumanBodyBones, Quaternion> BuildRelaxedCurl()
        {
            var map = new Dictionary<HumanBodyBones, Quaternion>(_segments.Length * 2);
            foreach (var (left, right, degrees) in _segments)
            {
                map[left] = degrees == 0f ? Quaternion.identity : Quaternion.Euler(0f, 0f, degrees);
                map[right] = degrees == 0f ? Quaternion.identity : Quaternion.Euler(0f, 0f, -degrees);
            }
            return map;
        }
    }
}
