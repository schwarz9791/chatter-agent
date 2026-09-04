using System;
using System.Collections.Generic;
using UnityEngine;
using UniVRM10;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// VRMA の <see cref="INormalizedPoseProvider"/> をラップし、ファイルに無い指ボーンだけ
    /// <see cref="FingerPose.RelaxedCurl(HumanBodyBones, double)"/> に差し替える。#88。
    ///
    /// ★ <b>なぜここが必要か。</b> <c>Vrm10Runtime.Process()</c> は
    ///   <c>Vrm10Retarget.Retarget(VrmAnimation.ControlRig, (ControlRig, ControlRig))</c> を
    ///   毎フレーム呼ぶ。<c>Retarget</c> は<b>モデル側</b>（<c>sink.TPose</c>＝30 指ボーンを含む）を
    ///   基準に走査し、各ボーンの回転を<b>VRMA 側</b>の <c>GetNormalizedLocalRotation</c> に問い合わせる。
    ///   同梱 <c>idle_loop.vrma</c> のような 22 ボーンのクリップは指を持たないので、
    ///   UniVRM の既定実装（<c>InitRotationPoseProvider</c>）はそこで <c>Quaternion.identity</c> を
    ///   返す。正規化空間での identity は VRM 1.0 の T ポーズ＝指がまっすぐ伸びた状態なので、
    ///   結果として「腕は下りているのに指だけ伸びきっている」不自然な見た目になる。
    /// ★ <b>なぜ <c>LateUpdate</c> で直接書けないか。</b> <c>Retarget</c> は
    ///   <c>Vrm10Instance.LateUpdate</c>（実行順 11000）の内部で ControlRig を毎フレーム
    ///   上書きする。他の実行順（<see cref="VrmPoseAccent"/> が 11005 に置く理由と同じ）を使わず、
    ///   <b>ソース側（VRMA の pose provider）を差し替える</b>のがいちばん単純で確実——
    ///   Retarget が呼ぶ相手そのものを変えるので、実行順の綱引きが要らない。
    /// ★ <b>なぜ「無いボーンだけ」か。</b> VRoid Studio 由来などボーン数の多い VRMA
    ///   （将来 #70 でクロスフェードするクリップを含む）は指を自前で持ちうる。
    ///   <see cref="ITPoseProvider.GetWorldTransform"/> が <c>HasValue == false</c> を返すのが
    ///   「このファイルにこのボーンは無い」ことの正確な判定なので、それだけを対象にする。
    ///   ファイル側の指があるのに上書きすると、せっかくの原作アニメーションを壊す。
    /// ★ #70 のクロスフェードでは、混ぜる各ソースにこの Wrap をそれぞれ掛けてから合成すること
    ///   （ここは1本の VRMA を対象にした処理で、複数ソースの合成そのものには関与しない）。
    /// ★ <b>#88 の後続。</b> 補った指は静止した丸めではなく <see cref="FingerPose.RelaxedCurl(HumanBodyBones, double)"/>
    ///   で時間ぶんだけ揺らす（体のアイドルとは結合しない、独立した時間ベース）。そのため
    ///   <see cref="_supplied"/> は補うボーンの<b>集合</b>（<c>HashSet</c>）に変わった——
    ///   角度は毎フレーム引数の <c>now</c> で変わるので、コンストラクタ時点の
    ///   <c>Quaternion</c> を1つ持ち回る形はもう成立しない。
    /// </summary>
    internal sealed class FingerFallbackPoseProvider : INormalizedPoseProvider
    {
        private readonly INormalizedPoseProvider _inner;
        private readonly HashSet<HumanBodyBones> _supplied;

        /// <summary>
        /// 「いま」を読む関数。<b>コンストラクタで注入する</b>——テストが固定値を渡せるようにするため、
        /// かつ <see cref="Wrap"/> が実体（<c>Time.realtimeSinceStartupAsDouble</c>）を1箇所で決めるため。
        /// </summary>
        private readonly Func<double> _clock;

        /// <summary>既定の丸めで補ったボーン数。0 なら全ての指をファイルが持っていた（差し替え不要）</summary>
        public int SuppliedCount => _supplied.Count;

        public FingerFallbackPoseProvider(INormalizedPoseProvider inner, ITPoseProvider tpose, Func<double> clock)
        {
            _inner = inner;
            _clock = clock;
            _supplied = new HashSet<HumanBodyBones>();

            foreach (var bone in FingerPose.FingerBones)
            {
                if (tpose.GetWorldTransform(bone).HasValue) continue; // ファイルがこのボーンを持っている
                _supplied.Add(bone);
            }
        }

        public Quaternion GetNormalizedLocalRotation(HumanBodyBones bone, HumanBodyBones parentBone)
        {
            return _supplied.Contains(bone)
                ? FingerPose.RelaxedCurl(bone, _clock())
                : _inner.GetNormalizedLocalRotation(bone, parentBone);
        }

        public Vector3 GetRawHipsPosition() => _inner.GetRawHipsPosition();

        /// <summary>
        /// <paramref name="vrma"/> の <c>ControlRig</c> をこのクラスでラップする。
        ///
        /// ★ <b>冪等。</b> <c>vrma.ControlRig.Item1</c> が既に <see cref="FingerFallbackPoseProvider"/>
        ///   ならラップし直さず、その <see cref="SuppliedCount"/> をそのまま返す
        ///   （<c>Adopt</c> が将来複数回呼ばれても二重ラップで <c>_inner</c> の連鎖が伸びない）。
        /// ★ 補うボーンが1本も無い（<c>SuppliedCount == 0</c>）ときは <c>ControlRig</c> に触れず
        ///   そのまま返す——差し替える意味が無い薄いラッパーを常に1枚挟むと、将来ここを
        ///   読む人が「何か特別なことをしているはず」と余計に疑う箇所が増えるだけになる。
        /// ★ <b>時計は <c>Time.realtimeSinceStartupAsDouble</c> に固定してここで注入する。</b>
        ///   <see cref="VrmCharacter.LateUpdate"/> が手続き的アイドルの <c>now</c> に使っているのと
        ///   同じ時計——<c>DateTimeOffset.UtcNow</c> は時計が巻き戻るとアイドルが凍る
        ///   （<c>VrmCharacter.cs:430-432</c> の理由と同じ）ので、こちらも同じ選択に揃える。
        /// </summary>
        public static int Wrap(Vrm10AnimationInstance vrma)
        {
            var (pose, tpose) = vrma.ControlRig;

            if (pose is FingerFallbackPoseProvider existing) return existing.SuppliedCount;

            var provider = new FingerFallbackPoseProvider(pose, tpose, () => Time.realtimeSinceStartupAsDouble);
            if (provider.SuppliedCount == 0) return 0;

            vrma.ControlRig = (provider, tpose);
            return provider.SuppliedCount;
        }
    }
}
