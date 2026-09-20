using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 起動時の頭の姿勢1回から、XR Origin の配置（空間固定）を求める。<b>純粋関数。</b>
    ///
    /// ★ <b>動かすのは Origin であって、キャラクター（<c>ModelAnchor</c>）ではない。</b>
    ///   <c>VrmStage.FaceCamera</c> が読み込み時にモデルをワールド−Zへ向ける処理をするので、
    ///   この起動時の配置でキャラ側を回すと打ち消される。見かけの配置は
    ///   「キャラは固定、頭の乗る Origin の方を位置とヨーだけ動かす」で作る
    ///   （読み込み後にキャラを動かす置き直しは <c>XrGrab</c> が別に行う）。
    /// </summary>
    public static class XrPlacement
    {
        /// <summary>
        /// 目からキャラまでの水平距離の下限（メートル）。キャラが頭の方を向くには、頭がキャラから
        /// 水平に離れている必要がある（真下や真上では向きが決まらない）。
        /// </summary>
        public const float MinHorizontalDistance = 0.2f;

        /// <summary>
        /// 頭の向きから、前方を水平面に投影したヨーと、上下の傾き（度）を出す。
        /// 真上・真下を向いていて前方が水平面に投影できないときのヨーは 0。
        /// </summary>
        /// <param name="yawDegrees">+Z から右回りが正</param>
        /// <param name="pitchDegrees">下向きが正（<see cref="TiltByHeadPitch"/> と同じ向き）</param>
        public static void HeadYawPitch(Quaternion headRotation, out float yawDegrees, out float pitchDegrees)
        {
            var forward = headRotation * Vector3.forward;
            var horizontal = new Vector3(forward.x, 0f, forward.z);
            yawDegrees = horizontal.sqrMagnitude > 1e-6f ? Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg : 0f;
            pitchDegrees = Mathf.Atan2(-forward.y, horizontal.magnitude) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// 目から足元へのずれ（水平 <paramref name="distance"/>・下へ <paramref name="feetBelowEye"/>）を、
        /// 頭の上下の傾きぶん回す。見下ろして起動しても、視界の同じ位置にキャラが出るようにするため。
        /// 回すのはずれの向きだけで、キャラ自身は傾けない（Origin はヨーしか回さない）。
        /// </summary>
        /// <param name="pitchDegrees">頭の上下の傾き（度）。下向きが正</param>
        public static void TiltByHeadPitch(
            float distance, float feetBelowEye, float pitchDegrees,
            out float tiltedDistance, out float tiltedFeetBelowEye)
        {
            var pitch = pitchDegrees * Mathf.Deg2Rad;
            var cos = Mathf.Cos(pitch);
            var sin = Mathf.Sin(pitch);

            tiltedDistance = Mathf.Max(distance * cos - feetBelowEye * sin, MinHorizontalDistance);
            tiltedFeetBelowEye = feetBelowEye * cos + distance * sin;
        }

        /// <param name="headLocalPosition">XR Origin 空間での頭の位置</param>
        /// <param name="headLocalYawDegrees">同じ空間で、頭の前方を水平面に投影したヨー（度）</param>
        /// <param name="characterFeet">キャラクター（<c>ModelAnchor</c>）のワールド位置</param>
        /// <param name="distance">目からキャラまでの水平距離（メートル）</param>
        /// <param name="azimuthDegrees">起動時の正面から右回りに何度の方向へキャラを置くか</param>
        /// <param name="feetBelowEye">キャラの足元が目より何 m 下か</param>
        /// <param name="originPosition">求まった XR Origin のワールド位置</param>
        /// <param name="originYawDegrees">求まった XR Origin のワールドヨー（度）</param>
        public static void Solve(
            Vector3 headLocalPosition, float headLocalYawDegrees, Vector3 characterFeet,
            float distance, float azimuthDegrees, float feetBelowEye,
            out Vector3 originPosition, out float originYawDegrees)
        {
            // キャラは −Z を向いているので、頭がキャラの正面（−Z 側）に来る
            var headWorldPosition = characterFeet + new Vector3(0f, feetBelowEye, -distance);

            originYawDegrees = -azimuthDegrees - headLocalYawDegrees;

            var rotation = Quaternion.Euler(0f, originYawDegrees, 0f);
            originPosition = headWorldPosition - rotation * headLocalPosition;
        }
    }
}
