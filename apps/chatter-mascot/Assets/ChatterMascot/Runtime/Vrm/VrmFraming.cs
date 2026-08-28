using UnityEngine;

namespace ChatterMascot.Vrm
{
    /// <summary>どちらの軸でカメラ距離が決まったか。ログに出して切り分けに使う。</summary>
    public enum FramingAxis
    {
        Vertical,
        Horizontal,
    }

    /// <summary>
    /// モデルが画面に収まるカメラ距離を出す。<b>純粋関数。</b>
    ///
    /// ★ <b><c>Camera.fieldOfView</c> は <c>m_FOVAxisMode</c> に関わらず常に「垂直」FOV。</b>
    ///   水平は <c>tan(hFov/2) = tan(vFov/2) * aspect</c> で決まるので、
    ///   <b>縦長のウィンドウほど横が狭くなる</b>。
    ///
    /// ★ <b>VRM 1.0 はレストポーズが T ポーズ必須。</b> 素の <c>Renderer.bounds</c> を渡すと
    ///   <c>bounds.extents.x</c> が「広げた腕」で決まり、縦長のウィンドウでは
    ///   <b>水平側が支配的になる</b>（同梱の vita.vrm で 1.39m × 1.73m）。しかも
    ///   <c>SkinnedMeshRenderer.bounds</c> は姿勢を反映しないので、アイドルモーションで
    ///   腕が下りても縮まない。
    /// ★ <b>だから #59 以降、渡ってくるのは <c>VrmStage.MeasureBounds</c> が
    ///   ボーンから測った箱</b>（<c>VrmBounds.IsFramingBone</c> が腕・手・指を除く）。
    ///   vita.vrm では 0.35m × 1.66m になり、<b>支配軸は垂直</b>。腕を含めると
    ///   読み込み直後の2秒だけ幅 1.5m 級の箱になり、VRMA が効いた瞬間に
    ///   カメラが寄って<b>キャラが一段大きくなるのが見える</b>（実機で踏んだポップ）。
    ///   <b>「小さく映る」の原因が腕なのか身長なのかはログでしか分からない</b>ので、
    ///   <see cref="Solve"/> は採用した軸も返す。
    /// </summary>
    public static class VrmFraming
    {
        /// <summary>
        /// 縦横どちらもはみ出さない距離を返す。
        /// </summary>
        /// <param name="bounds">モデルのワールド bounds</param>
        /// <param name="verticalFovDeg">カメラの垂直 FOV（度）</param>
        /// <param name="aspect">ビューポートの横÷縦（<c>Camera.aspect</c>）</param>
        /// <param name="headroom">余白の係数。1.0 でぴったり</param>
        public static float Solve(Bounds bounds, float verticalFovDeg, float aspect,
                                  float headroom, out FramingAxis axis)
        {
            axis = FramingAxis.Vertical;

            // 退化した入力（bounds が空、アスペクトが 0、FOV が範囲外）でカメラを飛ばさない
            if (aspect <= 0f || verticalFovDeg <= 0f || verticalFovDeg >= 180f) return 0f;

            var tanHalfVertical = Mathf.Tan(verticalFovDeg * 0.5f * Mathf.Deg2Rad);
            if (tanHalfVertical <= 0f) return 0f;

            var tanHalfHorizontal = tanHalfVertical * aspect;

            var vertical = bounds.extents.y / tanHalfVertical;
            var horizontal = bounds.extents.x / tanHalfHorizontal;
            if (horizontal > vertical) axis = FramingAxis.Horizontal;

            var distance = Mathf.Max(vertical, horizontal) * Mathf.Max(headroom, 0f);

            // ★ 奥行きの半分を足す。bounds の手前面が near clip に刺さると
            //   「顔だけ切り取られる」という分かりにくい壊れ方をする
            return distance + bounds.extents.z;
        }

        /// <summary>
        /// モデルの中心を見るカメラ位置。カメラは回転なしで +Z を向いている前提
        /// （glTF→Unity の Z 反転でモデルが −Z を向くので、これで顔がこちらを向く）。
        /// </summary>
        public static Vector3 CameraPosition(Bounds bounds, float distance) =>
            new Vector3(bounds.center.x, bounds.center.y, bounds.center.z - distance);
    }
}
