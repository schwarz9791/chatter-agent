namespace ChatterMascot.Xr
{
    /// <summary>
    /// 設定パネルの呼び出し口（頭上の歯車 / 手のひらのボタン）を出すかどうかの判定。
    /// <b>純粋関数。</b>
    ///
    /// ★ <b>当たり判定そのものはここに無い。</b> レイが何に当たったか・関節姿勢が取れているかは
    ///   呼び出し側（<c>XrSettingsBridge</c> / <c>XrHandTracking</c>）が決め、時刻や向きの内積
    ///   だけをここへ渡す。ここは経過時間の算数と、内積のヒステリシスだけを持つ。
    /// </summary>
    public static class XrMenuRules
    {
        /// <summary>
        /// 当たらなくなってもこの秒数は呼び出し口を出したままにする既定値。
        ///
        /// ★ キャラクターから歯車へレイを動かす途中で消えないための猶予。実測値ではなく既定値。
        /// </summary>
        public const float HoverGraceSeconds = 1.5f;

        /// <summary>
        /// 呼び出し口を出すか。
        ///
        /// ★ <b>パネルが開いている間は常に出さない。</b>
        /// ★ <b>当たっているかどうかは時刻だけで判定する。</b> 当たった側は、当たっている間
        ///   毎フレーム <paramref name="lastHitAt"/> を「いま」に進めること —— そうすれば
        ///   「いま当たっている」は「経過時間が 0 に近い」として自然に表現できる。
        /// </summary>
        public static bool ShowInvoker(bool panelOpen, double now, double lastHitAt, float graceSeconds = HoverGraceSeconds)
        {
            if (panelOpen) return false;
            return now - lastHitAt < graceSeconds;
        }

        /// <summary>
        /// 頭上の歯車を出すか。<see cref="ShowInvoker"/> と同じ判定に、<b>手のひらの向きが判定に
        /// 使える間は出さない</b>という条件を重ねる——関節が取れている間は手のひらメニューへ委ねる。
        /// </summary>
        public static bool ShowGear(
            bool panelOpen, bool handTrackingAvailable, double now, double lastHitAt, float graceSeconds = HoverGraceSeconds)
        {
            if (handTrackingAvailable) return false;
            return ShowInvoker(panelOpen, now, lastHitAt, graceSeconds);
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

        /// <summary>
        /// 手のひらの呼び出し口（ボタン）を出すか。<b>パネルが開いている間は出さない。</b>
        /// </summary>
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

        public static bool ShowPalmButton(bool panelOpen, bool handTrackingAvailable, bool palmFacingSelf)
        {
            if (panelOpen) return false;
            return handTrackingAvailable && palmFacingSelf;
        }
    }
}
