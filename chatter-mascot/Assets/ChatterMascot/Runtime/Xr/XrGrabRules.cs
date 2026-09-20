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
