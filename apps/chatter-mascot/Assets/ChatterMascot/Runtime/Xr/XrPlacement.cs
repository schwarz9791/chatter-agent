using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 起動時の頭の姿勢1回から、XR Origin の配置（空間固定）を求める。<b>純粋関数。</b>
    ///
    /// ★ <b>動かすのは Origin であって、キャラクター（<c>ModelAnchor</c>）ではない。</b>
    ///   <c>VrmStage.FaceCamera</c> がモデルをワールド−Zへ向ける処理をするので、
    ///   キャラ側を回すと読み込みのたびに打ち消される。見かけの配置は
    ///   「キャラは固定、頭の乗る Origin の方を位置とヨーだけ動かす」で作る。
    /// </summary>
    public static class XrPlacement
    {
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
            // キャラは −Z を向いているので、頭がキャラの正面（+Z 側）に来る
            var headWorldPosition = characterFeet + new Vector3(0f, feetBelowEye, -distance);

            originYawDegrees = -azimuthDegrees - headLocalYawDegrees;

            var rotation = Quaternion.Euler(0f, originYawDegrees, 0f);
            originPosition = headWorldPosition - rotation * headLocalPosition;
        }
    }
}
