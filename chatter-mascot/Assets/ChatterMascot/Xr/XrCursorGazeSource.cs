using ChatterMascot.Vrm;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 追跡されている手（またはマウス）の aim レイ（<see cref="XrGrab"/> に一本化された入力）を、
    /// <c>Desktop/CursorGazeSource</c> と同じ正規化にして <see cref="VrmCharacter.CursorProvider"/>
    /// へ渡す。手もマウスも追跡されていなければ <c>null</c>（呼び出し側は自律的な漂いに倒れる）。
    ///
    /// ★ <b>新しい Input System のバインドを増やさない。</b> レイの読み取りは <see cref="XrGrab"/> に
    ///   一本化したまま、ここは公開されたレイを座標変換するだけ。
    /// ★ <b>MonoBehaviour にしない。</b> <c>XrStage</c> が置き直しの直後に
    ///   <c>VrmCharacter.CursorProvider</c> へ関数として結線する（Desktop 側と同じ注入の形）。
    /// </summary>
    internal static class XrCursorGazeSource
    {
        /// <summary>
        /// 見る点は aim レイ上、キャラの距離の点（<see cref="PointNearModel"/>）。それをカメラの
        /// viewport へ投影し、<c>CursorGazeSource</c> と同じ考え方（顔の位置を中心にする）を
        /// viewport 空間で行う。横はビューポート中心ではなく<b>顔の水平位置</b>を基準にする——
        /// XR はデスクトップと違い、キャラがビューポート中心にいるとは限らない。
        /// </summary>
        public static Vector2? TryRead(XrGrab grab, XROrigin origin, VrmStage stage, VrmCharacter character)
        {
            if (grab == null || origin == null || origin.Camera == null) return null;
            if (stage == null || stage.ModelAnchor == null) return null;
            if (!grab.TryGetAimRay(out var ray)) return null;

            var point = PointNearModel(ray, stage);

            var viewport = origin.Camera.WorldToViewportPoint(point);
            // ★ カメラの後ろの点は WorldToViewportPoint で x/y の符号が反転する
            if (viewport.z <= 0f) return null;

            var viewportX = character != null ? character.GazeOriginViewportX : 0.5f;
            var viewportY = character != null ? character.GazeOriginViewportY : 0.5f;

            return new Vector2((viewport.x - viewportX) * 2f, (viewport.y - viewportY) * 2f);
        }

        /// <summary>
        /// aim レイ上の、キャラクターの距離にある点。キャラを見ている目のピントのまま追えるよう、
        /// 奥行きをキャラに揃える。
        ///
        /// ★ 距離は当たり判定の最も近い面までにする。足元の基準点までだと、カプセルからはみ出した
        ///   腕より奥に点が落ちる。当たり判定が無い・原点が内側にあるときは基準点までに倒れる。
        /// </summary>
        internal static Vector3 PointNearModel(Ray ray, VrmStage stage)
        {
            var collider = stage.Model != null ? stage.Model.GetComponentInChildren<Collider>() : null;
            var distance = collider != null ? Vector3.Distance(ray.origin, collider.ClosestPoint(ray.origin)) : 0f;
            if (distance < Mathf.Epsilon) distance = Vector3.Distance(ray.origin, stage.ModelAnchor.position);
            return ray.GetPoint(distance);
        }
    }
}
