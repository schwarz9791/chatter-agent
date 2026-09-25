using ChatterMascot.Vrm;
using Unity.XR.CoreUtils;
using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 追跡されている手の aim レイ（<see cref="XrGrab"/> に一本化された入力）を、
    /// <c>Desktop/CursorGazeSource</c> と同じ正規化にして <see cref="VrmCharacter.CursorProvider"/>
    /// へ渡す。手が追跡されていなければ <c>null</c>（呼び出し側は自律的な漂いに倒れる）。
    ///
    /// ★ <b>新しい Input System のバインドを増やさない。</b> レイの読み取りは <see cref="XrGrab"/> に
    ///   一本化したまま、ここは公開されたレイを座標変換するだけ。
    /// ★ <b>MonoBehaviour にしない。</b> <c>XrStage</c> が置き直しの直後に
    ///   <c>VrmCharacter.CursorProvider</c> へ関数として結線する（Desktop 側と同じ注入の形）。
    /// </summary>
    internal static class XrCursorGazeSource
    {
        /// <summary>
        /// 見る点は aim レイ上、キャラ（<c>ModelAnchor</c>）までの距離の点。それをカメラの
        /// viewport へ投影し、<c>CursorGazeSource</c> と同じ考え方（顔の位置を中心にする）を
        /// viewport 空間で行う。横はビューポート中心ではなく<b>顔の水平位置</b>を基準にする——
        /// XR はデスクトップと違い、キャラがビューポート中心にいるとは限らない。
        /// </summary>
        public static Vector2? TryRead(XrGrab grab, XROrigin origin, VrmStage stage, VrmCharacter character)
        {
            if (grab == null || origin == null || origin.Camera == null) return null;
            if (stage == null || stage.ModelAnchor == null) return null;
            if (!grab.TryGetAimRay(out var ray)) return null;

            var distance = Vector3.Distance(ray.origin, stage.ModelAnchor.position);
            var point = ray.GetPoint(distance);

            var viewport = origin.Camera.WorldToViewportPoint(point);
            // ★ カメラの後ろの点は WorldToViewportPoint で x/y の符号が反転する
            if (viewport.z <= 0f) return null;

            var viewportX = character != null ? character.GazeOriginViewportX : 0.5f;
            var viewportY = character != null ? character.GazeOriginViewportY : 0.5f;

            return new Vector2((viewport.x - viewportX) * 2f, (viewport.y - viewportY) * 2f);
        }
    }
}
