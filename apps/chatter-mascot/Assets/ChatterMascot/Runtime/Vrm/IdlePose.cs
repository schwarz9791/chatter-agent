using System;
using ChatterMascot.Protocol;
using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 手続き的アイドルモーションの振幅パラメータ。
    ///
    /// ★ <c>readonly struct</c>。テストが振幅 0 の恒等入力を作れるよう、
    ///   全フィールドを取るコンストラクタを持つ。
    /// </summary>
    public readonly struct IdleParams
    {
        /// <summary>呼吸の周期（秒）</summary>
        public readonly float BreathSeconds;

        /// <summary>腰の上下（メートル）。数 mm</summary>
        public readonly float BreathHipsMeters;

        /// <summary>胸の前後の傾き（度）</summary>
        public readonly float BreathChestDegrees;

        /// <summary>重心移動の周期A（秒）。<see cref="SwaySecondsB"/> と互いに素に近い値にして非周期に見せる</summary>
        public readonly float SwaySecondsA;

        /// <summary>重心移動の周期B（秒）</summary>
        public readonly float SwaySecondsB;

        /// <summary>重心移動の振幅上限（度）</summary>
        public readonly float SwayDegrees;

        /// <summary>首の微動の周期（秒）。呼吸・重心移動よりさらに遅い成分</summary>
        public readonly float NeckSeconds;

        /// <summary>
        /// 首の微動の振幅（度）。<b>Neck と Head を合わせた合計</b>。
        ///
        /// ★ Neck → Head は親子ボーンで、ControlRig のローカル回転はここで合成される。
        ///   この値をそのまま両方に入れると見た目は 2 倍になるので、
        ///   <see cref="IdlePose.Evaluate"/> が配分する（実際の分け方はそちらのコメント参照）。
        /// </summary>
        public readonly float NeckDegrees;

        /// <summary>発話中に全振幅へ掛けるゲイン</summary>
        public readonly float SpeakingGain;

        /// <summary>
        /// <c>kind == Prompt</c> のとき全振幅へ掛けるゲイン。
        ///
        /// ★ <b>前傾そのものはここに持たせない。</b> 前傾の持ち主は <c>VrmPoseAccent</c> 1箇所に
        ///   寄せる（VRMA が有効なとき <see cref="IdlePose"/> はそもそも呼ばれないので、
        ///   ここに前傾を持たせると VRMA の有無で prompt の見え方が変わってしまう）。
        ///   <see cref="IdlePose"/> が <c>kind</c> を受け取るのは<b>揺れを抑えるため</b>だけ。
        /// </summary>
        public readonly float PromptDamp;

        /// <summary>
        /// 上腕を体側へ下ろす、静止姿勢の角度（度）。ControlRig の正規化 T ポーズ
        /// （<c>localRotation == identity</c> ＝ 腕を真横に開いた状態）からの回転量。
        ///
        /// ★ <b><see cref="IdlePose.Evaluate"/> は Z 軸まわりの回転として実装する。
        ///   正の値のとき「右腕（<c>RightUpperArmEuler.z</c>）が体側へ下がる」向きを意図して書いた。</b>
        ///   ControlRig の正規化 T ポーズは左右対称に開いているので、左腕には符号を反転して
        ///   適用する（<c>LeftUpperArmEuler.z</c> は負）。実機のスクリーンショットで腕が
        ///   上がる／前後に振れるなど意図と違う向きに動いたら、まずこの正負を反転させて
        ///   確かめること（軸を変える前に、まず符号を疑う）。
        /// </summary>
        public readonly float RestUpperArmDegrees;

        /// <summary>
        /// 肘の軽い曲げ（度）。<see cref="RestUpperArmDegrees"/> と同じ Z 軸・同じ左右対称の
        /// 約束（正で右腕側が曲がる想定、左は符号反転）で実装する。
        /// </summary>
        public readonly float RestLowerArmDegrees;

        public IdleParams(
            float breathSeconds,
            float breathHipsMeters,
            float breathChestDegrees,
            float swaySecondsA,
            float swaySecondsB,
            float swayDegrees,
            float neckSeconds,
            float neckDegrees,
            float speakingGain,
            float promptDamp,
            float restUpperArmDegrees,
            float restLowerArmDegrees)
        {
            BreathSeconds = breathSeconds;
            BreathHipsMeters = breathHipsMeters;
            BreathChestDegrees = breathChestDegrees;
            SwaySecondsA = swaySecondsA;
            SwaySecondsB = swaySecondsB;
            SwayDegrees = swayDegrees;
            NeckSeconds = neckSeconds;
            NeckDegrees = neckDegrees;
            SpeakingGain = speakingGain;
            PromptDamp = promptDamp;
            RestUpperArmDegrees = restUpperArmDegrees;
            RestLowerArmDegrees = restLowerArmDegrees;
        }

        public static IdleParams Default => new IdleParams(
            breathSeconds: 4f,
            breathHipsMeters: 0.004f,
            breathChestDegrees: 1.0f,
            swaySecondsA: 7f,
            swaySecondsB: 11f,
            swayDegrees: 1.5f,
            neckSeconds: 13f,
            neckDegrees: 2.0f,
            speakingGain: 1.3f,
            promptDamp: 0.5f,
            restUpperArmDegrees: 70f,
            restLowerArmDegrees: 10f);
    }

    /// <summary>ある瞬間の手続き的アイドルの姿勢。単位は度（<c>HipsOffsetY</c> だけメートル）。</summary>
    public readonly struct IdlePoseSample
    {
        public readonly float HipsOffsetY;
        public readonly Vector3 SpineEuler;
        public readonly Vector3 ChestEuler;
        public readonly Vector3 NeckEuler;
        public readonly Vector3 HeadEuler;

        /// <summary>
        /// 上腕・前腕の静止姿勢（度）。<see cref="IdleParams.RestUpperArmDegrees"/> /
        /// <see cref="IdleParams.RestLowerArmDegrees"/> から作る、左右で符号が反転する Z 軸回転
        /// （ControlRig の正規化 T ポーズは左右対称に開いているため）。
        ///
        /// ★ 呼吸・重心移動の揺れはここには乗せていない。理由は <see cref="IdlePose.Evaluate"/> のコメント参照。
        /// </summary>
        public readonly Vector3 LeftUpperArmEuler;
        public readonly Vector3 RightUpperArmEuler;
        public readonly Vector3 LeftLowerArmEuler;
        public readonly Vector3 RightLowerArmEuler;

        public IdlePoseSample(
            float hipsOffsetY,
            Vector3 spineEuler,
            Vector3 chestEuler,
            Vector3 neckEuler,
            Vector3 headEuler,
            Vector3 leftUpperArmEuler,
            Vector3 rightUpperArmEuler,
            Vector3 leftLowerArmEuler,
            Vector3 rightLowerArmEuler)
        {
            HipsOffsetY = hipsOffsetY;
            SpineEuler = spineEuler;
            ChestEuler = chestEuler;
            NeckEuler = neckEuler;
            HeadEuler = headEuler;
            LeftUpperArmEuler = leftUpperArmEuler;
            RightUpperArmEuler = rightUpperArmEuler;
            LeftLowerArmEuler = leftLowerArmEuler;
            RightLowerArmEuler = rightLowerArmEuler;
        }
    }

    /// <summary>
    /// T ポーズの棒立ちを崩す、呼吸・重心移動・首の微動の手続き的アイドル。<b>純粋関数。</b>
    ///
    /// ★ <b>VRMA が有効な間は呼ばないこと。</b> VRMA は <c>Vrm10Retarget.Retarget</c> で
    ///   ControlRig の全ボーンを毎フレーム上書きするので、同じボーンに <see cref="IdlePose"/> の
    ///   結果も書くと奪い合いになる。呼び出し側（<c>VrmCharacter</c>）が <c>VrmIdleAnimation.IsPlaying</c>
    ///   を見て切り替える。
    ///
    /// ★ <b>時刻は <c>Time.realtimeSinceStartupAsDouble</c> を渡すこと。</b>
    ///   <c>DateTimeOffset.UtcNow</c> は時計が巻き戻るとアイドルが凍る
    ///   （<see cref="ChatterMascot.Audio.AudioIdleGate"/> と同じ理由）。
    /// </summary>
    public static class IdlePose
    {
        public static IdlePoseSample Evaluate(double now, in IdleParams p, SpeechKind kind, bool speaking)
        {
            var gain = speaking ? p.SpeakingGain : 1f;
            if (kind == SpeechKind.Prompt) gain *= p.PromptDamp;

            // 呼吸: 腰の上下と胸の前後の傾き
            var breathSin = Mathf.Sin(Phase(now, p.BreathSeconds));
            var hipsOffsetY = breathSin * p.BreathHipsMeters * gain;
            var chestX = breathSin * p.BreathChestDegrees * gain;

            // 重心移動: 周期の違う2本の sin を 0.5 ずつ足して非周期に見せる。
            // それぞれ振幅 1 の sin を 0.5 倍ずつ足すので、両方が山で重なっても
            // 合成値は ±1 を超えず、SwayDegrees 以内に収まる。
            var swayA = Mathf.Sin(Phase(now, p.SwaySecondsA));
            var swayB = Mathf.Sin(Phase(now, p.SwaySecondsB));
            var swayZ = (swayA * 0.5f + swayB * 0.5f) * p.SwayDegrees * gain;

            // 首の微動: さらに遅い成分。左右への回旋（yaw = y軸）として持たせる。
            // 呼吸（x軸のピッチ）・重心移動（z軸のロール）と軸を分けて、動きの種類が
            // 重ならないようにしてある。
            //
            // ★ Neck → Head は親子ボーンなので、ControlRig のローカル回転はここで合成される。
            //   NeckDegrees を「Neck と Head を合わせた合計」の振幅として、0.6 / 0.4 に配分する
            //   （首の付け根が主、頭の先が従）。同じ値を両方に入れると見た目は 2×NeckDegrees になる
            //   ——チャンネル単体のテストでは緑になるが実際の可動域は仕様の2倍という、
            //   いちばん質の悪い壊れ方をする。
            const float NeckShare = 0.6f;
            var neckAmplitude = Mathf.Sin(Phase(now, p.NeckSeconds)) * p.NeckDegrees * gain;
            var neckEuler = new Vector3(0f, neckAmplitude * NeckShare, 0f);
            var headEuler = new Vector3(0f, neckAmplitude * (1f - NeckShare), 0f);

            // 腕の静止姿勢: T ポーズ（ControlRig の identity）から腕を体側へ下ろす。
            //
            // ★ ここには呼吸・重心移動の揺れをあえて乗せない。上腕・前腕は肩→肘の
            //   2ボーンチェーンで、それぞれに独立した sin を足すと肩と肘が別々に揺れて
            //   振り子のようにブラブラして見えるリスクがある（腕だけ別の生き物のように
            //   動いて見える）。#59 のいちばんの目的は T ポーズの解消であって腕の芝居では
            //   ないので、まず静止姿勢そのものの符号・角度を実機で確定させることを優先し、
            //   揺れは乗せない。乗せるなら、ここに体幹（SwayDegrees）より小さい振幅で
            //   別の sin を足す形になる。
            // ★ Z 軸まわりの回転。**符号は実機のスクリーンショットで決めた。導出で書き換えないこと。**
            //   最初 `右 = +RestUpperArmDegrees` で書いたところ、実機では**腕が万歳の向きに上がった**
            //   （同梱 VRMA を退避してフォールバックさせて確認）。反転させて体側へ下りることを確認済み。
            //   ControlRig の正規化 T ポーズは左右対称に開いているので、左腕は符号を反転して適用する。
            var rightUpperArmEuler = new Vector3(0f, 0f, -p.RestUpperArmDegrees);
            var leftUpperArmEuler = new Vector3(0f, 0f, p.RestUpperArmDegrees);
            var rightLowerArmEuler = new Vector3(0f, 0f, -p.RestLowerArmDegrees);
            var leftLowerArmEuler = new Vector3(0f, 0f, p.RestLowerArmDegrees);

            return new IdlePoseSample(
                hipsOffsetY,
                new Vector3(0f, 0f, swayZ),
                new Vector3(chestX, 0f, 0f),
                neckEuler,
                headEuler,
                leftUpperArmEuler,
                rightUpperArmEuler,
                leftLowerArmEuler,
                rightLowerArmEuler);
        }

        /// <summary>
        /// 2π t / period。周期が 0 以下なら止まっているものとして 0 を返す。
        ///
        /// ★ <b>float へ落とす前に周期で畳むこと。</b> このアプリは常駐するので
        ///   <c>now</c>（<c>Time.realtimeSinceStartupAsDouble</c>）は日単位まで伸びる。
        ///   畳まずに <c>(float)(2π·now/period)</c> とすると、7日で位相が 95万に達し、
        ///   そこでの float の刻み幅（約 0.0625 rad ＝ 3.6°）が1フレームぶんの位相差を
        ///   上回る。症状は「何日か点けっぱなしにすると呼吸がカクつく／止まって見える」で、
        ///   <b>エラーは出ない</b>。
        /// </summary>
        private static float Phase(double now, float periodSeconds)
        {
            if (periodSeconds <= 0f) return 0f;
            var wrapped = now - Math.Floor(now / periodSeconds) * periodSeconds;
            return (float)(2.0 * Math.PI * wrapped / periodSeconds);
        }
    }
}
