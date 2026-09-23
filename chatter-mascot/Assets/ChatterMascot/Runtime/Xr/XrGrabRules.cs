using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// つまみ操作の判定とヨー計算。<b>純粋関数。</b>
    ///
    /// ★ テスト asmdef が <c>ChatterMascot.Xr</c> を参照しないので、<see cref="XrPlacement"/> と
    ///   同じく Runtime に置く（アセンブリの所属はフォルダの位置で決まり、名前空間では決まらない）。
    /// </summary>
    public static class XrGrabRules
    {
        /// <summary>Hand Interaction Profile の <c>pinchValue</c>（0..1、1 がつまみ切り）がこれ以上でつまみに入る。</summary>
        public const float PinchEnterValue = 0.9f;

        /// <summary>つまみ中は、<c>pinchValue</c> がこれを下回るまで抜けたと判定しない。震え対策のヒステリシス。</summary>
        public const float PinchExitValue = 0.6f;

        /// <summary>ヒステリシス付きのつまみ判定。</summary>
        public static bool IsPinching(bool wasPinching, float pinchValue)
        {
            return wasPinching
                ? pinchValue >= PinchExitValue
                : pinchValue >= PinchEnterValue;
        }

        /// <summary>レイの俯角がこれ未満なら、掴んだときの距離のまま動かす。</summary>
        public const float DepthBlendStartDegrees = 10f;

        /// <summary>レイの俯角がこれ以上なら、沿わせる水平面との交点まで動かす。</summary>
        public const float DepthBlendEndDegrees = 20f;

        /// <summary>水平面との交点を追うときの距離の上限（m）。水平に近いレイで遠くへ飛ばさない。</summary>
        public const float MaxHeldDistance = 3f;

        /// <summary>
        /// つまんでいる間、aim レイに沿って掴んだ点をどこまで先に置くか。
        ///
        /// ★ <b>水平面とレイの交点を追う。</b> 距離を固定すると、奥の床を指して手を下げても
        ///   空中の手前に留まり、離したとき真下へ落ちて狙いより手前に着く。交点を追えば、
        ///   手の上下が奥行きになり、キャラは床と平行に動く。
        /// ★ 面が掴んだ点の高さなら、掴んだ瞬間の交点は掴んだ点そのものなので跳ばない。水平に近い・
        ///   上向きのレイは交点が遠くへ発散するので、俯角で掴んだときの距離となめらかに混ぜる。
        /// </summary>
        /// <param name="planeHeight">掴んだ点を沿わせる水平面のワールドの高さ。</param>
        /// <param name="grabDistance">掴んだ瞬間の、レイに沿ったヒット距離。</param>
        public static float HeldDistance(Ray ray, float planeHeight, float grabDistance)
        {
            var sinDown = -ray.direction.y;
            var blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(
                Mathf.Sin(DepthBlendStartDegrees * Mathf.Deg2Rad),
                Mathf.Sin(DepthBlendEndDegrees * Mathf.Deg2Rad),
                sinDown));
            if (blend <= 0f) return grabDistance;

            var planeDistance = (ray.origin.y - planeHeight) / sinDown;
            if (planeDistance <= 0f) return grabDistance;

            planeDistance = Mathf.Min(planeDistance, Mathf.Max(MaxHeldDistance, grabDistance));
            return Mathf.Lerp(grabDistance, planeDistance, blend);
        }

        /// <summary>
        /// ローカル −Z を正面とする Transform を、<paramref name="from"/> から見て
        /// <paramref name="to"/> の水平方向へ向けるのに要る、ワールドの上軸まわりのヨー（度）。
        /// </summary>
        /// <returns><paramref name="from"/> と <paramref name="to"/> の水平距離がほぼ 0 なら false。</returns>
        public static bool TryYawToFace(Vector3 from, Vector3 to, out float yawDegrees)
        {
            var dx = to.x - from.x;
            var dz = to.z - from.z;
            if (dx * dx + dz * dz < 1e-8f)
            {
                yawDegrees = 0f;
                return false;
            }

            yawDegrees = Mathf.Atan2(-dx, -dz) * Mathf.Rad2Deg;
            return true;
        }
    }
}
