using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// つまみ操作の判定・つまんでいる間の奥行き・ヨー計算。<b>純粋関数。</b>
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

        private static readonly float SinDepthBlendStart = Mathf.Sin(DepthBlendStartDegrees * Mathf.Deg2Rad);
        private static readonly float SinDepthBlendEnd = Mathf.Sin(DepthBlendEndDegrees * Mathf.Deg2Rad);

        /// <summary>水平面との交点を追うときの距離の上限（m）。水平に近いレイで遠くへ飛ばさない。</summary>
        public const float MaxHeldDistance = 3f;

        /// <summary>手が沿わせる面よりこの高さ（m）以上あれば、俯角による混ぜをそのまま使う。
        /// これを下回るほど混ぜを弱め、面の高さに着くまでに掴んだ距離へなめらかに戻す。</summary>
        public const float MinHeightAbovePlane = 0.1f;

        /// <summary>
        /// つまんでいる間、aim レイに沿って掴んだ点をどこまで先に置くか。手の上下がそのまま
        /// 奥行きになり、キャラは床と平行に動く。水平に近い・上向きのレイは交点が遠くへ
        /// 発散するので、俯角で掴んだときの距離となめらかに混ぜる。手が面の高さに近づくほど
        /// 混ぜを弱め、掴んだ距離へ寄せる。
        /// </summary>
        /// <param name="planeHeight">掴んだ点を沿わせる水平面のワールドの高さ。</param>
        /// <param name="grabDistance">掴んだ瞬間の、レイに沿ったヒット距離。</param>
        public static float HeldDistance(Ray ray, float planeHeight, float grabDistance)
        {
            var sinDown = -ray.direction.y;
            var blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(SinDepthBlendStart, SinDepthBlendEnd, sinDown));
            blend *= Mathf.InverseLerp(0f, MinHeightAbovePlane, ray.origin.y - planeHeight);
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
