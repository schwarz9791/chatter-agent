using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 設定パネルの呼び出し口（頭上の歯車 / 手のひらのボタン）を出すかどうかの判定と、歯車の
    /// 置き方（大きさ・頭上の高さ・当たり判定の半径）。<b>純粋関数。</b>
    ///
    /// ★ <b>当たったかどうかの判定はここに無い。</b> レイが何に当たったか・関節姿勢が取れているかは
    ///   呼び出し側（<c>XrSettingsBridge</c> / <c>XrHandTracking</c>）が決め、時刻や向きの内積
    ///   だけをここへ渡す。ここは経過時間の算数・内積のヒステリシス・表示身長からの寸法だけを持つ。
    /// </summary>
    public static class XrMenuRules
    {
        /// <summary>
        /// キャラクターをつまんだ・離した後、頭上の歯車を出しておく秒数。既定値。
        ///
        /// ★ 歯車へレイを動かして押すまでの猶予。歯車に当たっている間は延び続ける。
        /// ★ 歩行範囲の円（<c>XrWalk</c>）も同じ秒数を使う。
        /// </summary>
        public const float GearVisibleSeconds = 10f;

        /// <summary>
        /// 呼び出し口を出すか。
        ///
        /// ★ <b>パネルが開いている間は常に出さない。</b>
        /// ★ <b>当たっているかどうかは時刻だけで判定する。</b> 当たった側は、当たっている間
        ///   毎フレーム <paramref name="lastHitAt"/> を「いま」に進めること —— そうすれば
        ///   「いま当たっている」は「経過時間が 0 に近い」として自然に表現できる。
        /// </summary>
        public static bool ShowInvoker(bool panelOpen, double now, double lastHitAt, float graceSeconds = GearVisibleSeconds)
        {
            if (panelOpen) return false;
            return now - lastHitAt < graceSeconds;
        }

        /// <summary>関節を一瞬見失っても手のひらの向きの判定を保つ猶予（秒）。既定値。</summary>
        public const float PalmTrackingGraceSeconds = 0.5f;

        /// <summary>
        /// 手のひらの向きの判定に使える状態か。<paramref name="lastTrackedAt"/> は、関節姿勢が
        /// 取れているフレームで呼び出し側が「いま」に更新すること——<see cref="ShowInvoker"/> と
        /// 同じ「経過時間で見る」形。
        /// </summary>
        public static bool HandTrackingAvailable(double now, double lastTrackedAt, float graceSeconds = PalmTrackingGraceSeconds)
        {
            return now - lastTrackedAt < graceSeconds;
        }

        /// <summary>手のひらを自分（頭）へ向けたと判定する内積の閾値（入り）。既定値。</summary>
        public const float PalmFacingEnterDot = 0.6f;

        /// <summary>同（抜け）。入りより緩め、境界の往復でちらつかないようにする。既定値。</summary>
        public const float PalmFacingExitDot = 0.3f;

        /// <summary>
        /// 手のひらの法線と「手のひら → 頭」の向きの内積から、自分へ向けたかを判定する。
        /// ヒステリシス付き（入りと抜けで閾値が違う）。
        /// </summary>
        public static bool IsPalmFacingSelf(bool wasFacingSelf, float dot)
        {
            return wasFacingSelf ? dot > PalmFacingExitDot : dot > PalmFacingEnterDot;
        }

        /// <summary>パネルが視線からこの角度（度）を超えて外れたら、正面へ戻し始める。既定値。</summary>
        public const float PanelFollowStartDegrees = 15f;

        /// <summary>戻し始めたパネルは、視線からこの角度（度）以内に入ったら止める。既定値。</summary>
        public const float PanelFollowStopDegrees = 5f;

        /// <summary>
        /// パネルを視線の正面へ戻すか。<b>ヒステリシス付き。</b>
        ///
        /// ★ 常に追従させない。少し視線を動かしただけで付いてくると、読んでいる行や
        ///   指そうとした行が逃げる。大きく外れたときだけ戻し、正面近くまで来たら止める。
        /// </summary>
        public static bool ShouldFollowPanel(bool wasFollowing, float degreesFromGaze)
        {
            return wasFollowing ? degreesFromGaze > PanelFollowStopDegrees : degreesFromGaze > PanelFollowStartDegrees;
        }

        /// <summary>
        /// 手のひらの呼び出し口（ボタン）を出すか。<b>パネルが開いている間は出さない。</b>
        /// </summary>
        public static bool ShowPalmButton(bool panelOpen, bool handTrackingAvailable, bool palmFacingSelf)
        {
            if (panelOpen) return false;
            return handTrackingAvailable && palmFacingSelf;
        }

        /// <summary>歯車の大きさが1倍になる、キャラクターの表示身長（m）。既定値。</summary>
        public const float GearReferenceHeightMeters = 0.25f;

        /// <summary>歯車の大きさの上限（倍率）。既定値。</summary>
        public const float GearScaleMax = 3f;

        /// <summary>
        /// キャラクターの表示身長に応じた歯車の倍率。
        ///
        /// ★ 身長にそのまま比例させると、等身大まで大きくしたときに歯車が大きくなりすぎるので、
        ///   平方根で伸びを緩める。
        /// </summary>
        public static float GearScale(float characterHeightMeters)
        {
            if (!float.IsFinite(characterHeightMeters) || characterHeightMeters <= 0f) return 1f;
            return Mathf.Clamp(Mathf.Sqrt(characterHeightMeters / GearReferenceHeightMeters), 1f, GearScaleMax);
        }

        /// <summary>呼び出し口（歯車・手のひらボタン）の見た目の大きさ（m）。基本の大きさ。既定値。</summary>
        public const float InvokerWorldSizeMeters = 0.035f;

        /// <summary>呼び出し口の当たり判定の半径（m）。見た目より大きく取る。既定値。</summary>
        public const float InvokerHitRadiusMeters = 0.05f;

        /// <summary>頭頂と歯車の下端の間隔を、キャラクターの表示身長のこの割合にする。既定値。</summary>
        public const float GearGapToHeightRatio = 0.1f;

        /// <summary><see cref="GearGapToHeightRatio"/> で決めた間隔の下限（m）。既定値。</summary>
        public const float GearMinGapMeters = 0.01f;

        /// <summary>
        /// 頭頂から歯車の<b>中心</b>までの高さ（m）。
        ///
        /// ★ 間隔は歯車の<b>下端</b>から測る。中心から測ると、歯車を大きくしたぶん下端が頭へ
        ///   食い込む。
        /// </summary>
        public static float GearHeightAboveHead(float characterHeightMeters)
        {
            var valid = float.IsFinite(characterHeightMeters) && characterHeightMeters > 0f;
            var gap = valid ? Mathf.Max(GearMinGapMeters, characterHeightMeters * GearGapToHeightRatio) : GearMinGapMeters;
            return gap + InvokerWorldSizeMeters * 0.5f * GearScale(characterHeightMeters);
        }

        /// <summary>
        /// 歯車の当たり判定の半径（m）。
        ///
        /// ★ 球を頭頂より下へ出さない。つまみは歯車を先に判定する
        ///   （<c>XrGrab.TryGrab</c> → <c>XrSettingsBridge.TryHandlePinch</c>）ので、球が頭へ
        ///   かかっていると、歯車が出ている間に頭をつまむとパネルが開いてしまう。
        /// ★ 限界: 見下ろしてつまむと、頭の上面を狙ったレイは頭に届く前に球を通るので歯車に
        ///   取られる（見下ろす角度が深いほど範囲が広い）。キャラと歯車の近い方を優先しても、
        ///   球の方が手前で当たるので直らない。真上からつまみ上げることはまず無いので、歯車を
        ///   頭の近くに出すことを優先している。
        /// </summary>
        public static float GearHitRadius(float characterHeightMeters)
        {
            return Mathf.Min(InvokerHitRadiusMeters * GearScale(characterHeightMeters), GearHeightAboveHead(characterHeightMeters));
        }
    }
}
