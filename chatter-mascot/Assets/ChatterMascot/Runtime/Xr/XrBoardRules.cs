using System.Collections.Generic;
using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>レイが板のどこに当たったか。</summary>
    public enum BoardPart
    {
        None,
        Body,
        Corner,
    }

    /// <summary>画面を映す板の初期配置・当たり判定・ドラッグ、部屋の減光が届いたかの判定。<b>純粋関数。</b> Physics は使わない。</summary>
    public static class XrBoardRules
    {
        /// <summary>板の長辺の既定（m）。</summary>
        public const float DefaultLongSide = 1.2f;

        /// <summary>最初に置くときの、視点から板の中心までの距離（m）。</summary>
        public const float DefaultDistance = 1.5f;

        public const float MinLongSide = 0.3f;
        public const float MaxLongSide = 3f;

        /// <summary>真上・真下で <c>LookRotation</c> が定まらなくなるため、板を上げ下げできる範囲を絞る。</summary>
        public const float MaxElevationDegrees = 60f;

        /// <summary>真上・真下に近い点ではヨーが定まらず、少し動いただけで板が大きく飛ぶ。これより上下を指している間は動かさない。</summary>
        public const float MaxPointElevationDegrees = 75f;

        /// <summary>角をつまめる範囲の半径（m）。</summary>
        public const float CornerGrabRadius = 0.05f;

        /// <summary>減光の読み返しと丸め先の差がこれ以内なら一致とみなす。</summary>
        private const float DimmingTolerance = 0.01f;

        private const float Epsilon = 1e-4f;

        /// <summary>
        /// 顔の向きの水平な正面、目の高さに置き、水平のまま視点へ正対させる。
        /// 真上・真下などで水平方向が定まらないときは +Z を使う。
        /// </summary>
        public static (Vector3 position, Quaternion rotation) InitialPose(Vector3 viewer, Vector3 viewerForward)
        {
            var forward = Flatten(viewerForward);
            if (forward == Vector3.zero) forward = Vector3.forward;
            return (viewer + forward * DefaultDistance, Quaternion.LookRotation(forward, Vector3.up));
        }

        /// <summary>
        /// レイが板のどこに当たるか。平面とは両面から交わる。角は本体より優先し、
        /// <paramref name="cornersEnabled"/> が false なら角は無いものとして本体だけを見る。
        /// </summary>
        public static BoardPart Pick(
            Ray ray, Vector3 center, Quaternion rotation, Vector2 size, bool cornersEnabled, out float distance)
        {
            distance = 0f;
            if (!TryPlanePoint(ray, center, rotation, out var point)) return BoardPart.None;
            distance = Vector3.Distance(ray.origin, point);

            var local = Quaternion.Inverse(rotation) * (point - center);
            var dx = Mathf.Abs(local.x) - size.x * 0.5f;
            var dy = Mathf.Abs(local.y) - size.y * 0.5f;
            if (cornersEnabled && dx * dx + dy * dy <= CornerGrabRadius * CornerGrabRadius) return BoardPart.Corner;
            return dx <= 0f && dy <= 0f ? BoardPart.Body : BoardPart.None;
        }

        /// <summary>レイと板の平面の交点。平行か、平面が背後なら false。</summary>
        public static bool TryPlanePoint(Ray ray, Vector3 center, Quaternion rotation, out Vector3 point)
        {
            point = default;
            var plane = new Plane(rotation * Vector3.forward, center);
            if (!plane.Raycast(ray, out var distance)) return false;
            point = ray.GetPoint(distance);
            return true;
        }

        /// <summary>
        /// 角をつまんだ点 <paramref name="grabPoint"/> から中心までの距離に対する、いまの点の
        /// 中心方向への進み具合を倍率にした長辺。中心の反対側へ行っても下限で止まる。
        /// </summary>
        public static float ResizedLongSide(float longSideAtGrab, Vector3 center, Vector3 grabPoint, Vector3 point)
        {
            var from = grabPoint - center;
            if (from.sqrMagnitude < Epsilon * Epsilon) return longSideAtGrab;
            var scale = Vector3.Dot(point - center, from) / from.sqrMagnitude;
            return Mathf.Clamp(longSideAtGrab * scale, MinLongSide, MaxLongSide);
        }

        /// <summary>長辺と縦横比（高さ / 幅）から板の大きさを求める。縦長の映像でも長辺の長さがそろう。</summary>
        public static Vector2 Size(float longSide, float aspect)
        {
            var width = longSide / Mathf.Max(1f, aspect);
            return new Vector2(width, width * aspect);
        }

        /// <summary>レイが球を指す点への、頭からのベクトル。交わらないか、真上・真下に近ければ false。</summary>
        internal static bool TryDragPoint(Ray ray, Vector3 head, float radius, out Vector3 toPoint)
        {
            toPoint = default;
            if (!TrySphereFar(ray, head, radius, out var point)) return false;
            toPoint = point - head;
            return Mathf.Abs(Elevation(toPoint)) <= MaxPointElevationDegrees;
        }

        /// <summary>レイと球の交点のうち遠い方。始点が球の内でも外でも連続する。</summary>
        internal static bool TrySphereFar(Ray ray, Vector3 center, float radius, out Vector3 point)
        {
            point = default;
            var oc = ray.origin - center;
            var b = Vector3.Dot(oc, ray.direction);
            var c = Vector3.Dot(oc, oc) - radius * radius;
            var disc = b * b - c;
            if (disc < 0f) return false;
            var t = -b + Mathf.Sqrt(disc);
            if (t < 0f) return false;
            point = ray.GetPoint(t);
            return true;
        }

        internal static float Yaw(Vector3 v) => Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;

        public static float Elevation(Vector3 v) =>
            Mathf.Asin(Mathf.Clamp(v.y / v.magnitude, -1f, 1f)) * Mathf.Rad2Deg;

        internal static Vector3 Direction(float yawDegrees, float elevationDegrees)
        {
            var y = yawDegrees * Mathf.Deg2Rad;
            var e = elevationDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(e) * Mathf.Sin(y), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Cos(y));
        }

        private static Vector3 Flatten(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude < 1e-4f ? Vector3.zero : v.normalized;
        }

        /// <summary>
        /// 減光の要求が届いたか。要求を対応レベルへどう丸めるかはランタイムしだいなので、
        /// 最も近いレベルでも、要求以下で最も近いレベルでも届いたとみなす。
        /// 対応レベルが無いときは任意の値を出せるので、要求そのものと比べる。
        /// </summary>
        public static bool DimmingReached(float current, float requested, IReadOnlyList<float> levels)
        {
            var gap = levels.Count == 0 ? 0f : float.MaxValue;
            var below = float.NegativeInfinity;
            foreach (var level in levels)
            {
                gap = Mathf.Min(gap, Mathf.Abs(level - requested));
                if (level <= requested) below = Mathf.Max(below, level);
            }
            return Mathf.Abs(current - requested) <= gap + DimmingTolerance
                || Mathf.Abs(current - below) <= DimmingTolerance;
        }
    }

    /// <summary>
    /// 頭を中心にした球の上で板を動かす。つまんだ点と中心の角度のずれを覚えておき、
    /// 動かしている間はレイと球の交点にそのずれを足した方角へ置く（距離は変わらず、頭へ向く）。
    /// </summary>
    public readonly struct BoardDrag
    {
        private readonly float _radius;
        private readonly float _yawOffset;
        private readonly float _elevationOffset;

        private BoardDrag(float radius, float yawOffset, float elevationOffset)
        {
            _radius = radius;
            _yawOffset = yawOffset;
            _elevationOffset = elevationOffset;
        }

        public static bool TryBegin(Ray ray, Vector3 head, Vector3 center, out BoardDrag drag)
        {
            drag = default;
            var toCenter = center - head;
            var radius = toCenter.magnitude;
            if (radius < 1e-3f) return false;
            if (!XrBoardRules.TryDragPoint(ray, head, radius, out var toGrab)) return false;

            drag = new BoardDrag(
                radius,
                Mathf.DeltaAngle(XrBoardRules.Yaw(toGrab), XrBoardRules.Yaw(toCenter)),
                XrBoardRules.Elevation(toCenter) - XrBoardRules.Elevation(toGrab));
            return true;
        }

        /// <summary>交わらないか、真上・真下に近ければ false。呼び出し側は最後の姿勢を保つ。</summary>
        public bool TryMove(Ray ray, Vector3 head, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = default;
            if (!XrBoardRules.TryDragPoint(ray, head, _radius, out var toPoint)) return false;

            var yaw = XrBoardRules.Yaw(toPoint) + _yawOffset;
            var elevation = Mathf.Clamp(
                XrBoardRules.Elevation(toPoint) + _elevationOffset,
                -XrBoardRules.MaxElevationDegrees, XrBoardRules.MaxElevationDegrees);
            var direction = XrBoardRules.Direction(yaw, elevation);
            position = head + direction * _radius;
            rotation = Quaternion.LookRotation(direction, Vector3.up);
            return true;
        }
    }
}
