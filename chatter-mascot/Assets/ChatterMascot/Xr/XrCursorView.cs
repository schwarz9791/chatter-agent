using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// aim レイの先に出すカーソルを描くだけの係。<b>何を指しているかは決めない。</b>
    /// 位置と状態は <see cref="XrGrab"/> が、つまみが作用する対象と同じ判定から渡す。
    ///
    /// ★ <b>ワールドに置く。<c>ModelAnchor</c> の子にしない</b> —— 縮尺が掛かるうえキャラと一緒に
    ///   動いてしまう。
    /// ★ <b>明るい色だけを使う。</b> グラスは加算表示なので、暗い色は透けて見えなくなる。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class XrCursorView : MonoBehaviour
    {
        /// <summary>カーソルの見た目。つまんだら何が起きるかを表す。</summary>
        public enum Look
        {
            /// <summary>何にも当たっていない。つまんでも何も起きない（塗りなしのリング）。</summary>
            Miss,

            /// <summary>押す・指す対象（設定パネル・歯車・平面など）。</summary>
            Point,

            /// <summary>つまめば掴む対象（キャラクター・板・歩行範囲のハンドル）。</summary>
            Grab,
        }

        // ── 見た目の既定値（実測値ではない） ─────────────────

        /// <summary>カメラから見た半径（rad）。距離に比例させ、見かけの大きさを一定にする。</summary>
        private const float AngularRadius = 0.006f;

        /// <summary>つかめる対象の上にいるときの倍率。</summary>
        private const float GrabbableScale = 1.3f;

        /// <summary>つまんでいる間の倍率。</summary>
        private const float PinchingScale = 0.75f;

        /// <summary>大きさが目標へ寄る速さ（1/秒）。</summary>
        private const float ScaleFollowRate = 20f;

        /// <summary>面に埋もれないよう、カメラ側へ持ち上げる距離。</summary>
        private const float LiftMeters = 0.004f;

        private const float BorderInnerRadius = 0.78f;
        private const int FillRenderQueue = 3100;
        private const int BorderRenderQueue = 3101;

        private static readonly Color FillColor = new Color(0.94f, 0.94f, 0.94f, 1f);
        private static readonly Color BorderColor = new Color(0.62f, 0.62f, 0.64f, 1f);
        private static readonly Color BorderGrabbableColor = new Color(0.66f, 0.78f, 0.98f, 1f);

        private Transform _disc;
        private Transform _border;
        private Material _fillMaterial;
        private Material _borderMaterial;

        private bool _built;
        private bool _buildFailed;
        private bool _shown;
        private Look _look;
        private float _scale;

        /// <summary>カーソルを <paramref name="point"/> に出す。毎フレーム呼ぶ。</summary>
        public void Show(Vector3 point, Vector3 cameraPosition, Look look, bool pinching)
        {
            if (!_built) Build();
            if (_buildFailed) return;

            var toCamera = cameraPosition - point;
            var distance = toCamera.magnitude;
            if (distance < 1e-4f)
            {
                Hide();
                return;
            }

            var normal = toCamera / distance;
            var target = (look == Look.Grab ? GrabbableScale : 1f) * (pinching ? PinchingScale : 1f);
            // ★ 出し直したときは目標の大きさから始める（前回の大きさから伸び縮みさせない）
            _scale = _shown
                ? Mathf.Lerp(_scale, target, 1f - Mathf.Exp(-ScaleFollowRate * Time.unscaledDeltaTime))
                : target;

            if (!_shown || look != _look)
            {
                // ★ 塗りの無いリングは縁の色だけで見えるので、塗りと同じ明るい色にする
                _borderMaterial.SetColor("_BaseColor",
                    look == Look.Grab ? BorderGrabbableColor : look == Look.Miss ? FillColor : BorderColor);
                _disc.gameObject.SetActive(look != Look.Miss);
                _look = look;
            }

            // ★ メッシュは XZ 平面で法線が +Y。+Y をカメラへ向ける
            transform.SetPositionAndRotation(
                point + normal * LiftMeters,
                Quaternion.FromToRotation(Vector3.up, normal));
            var radius = distance * AngularRadius * _scale;
            _disc.localScale = new Vector3(radius, 1f, radius);
            _border.localScale = new Vector3(radius, 1f, radius);

            if (!_shown)
            {
                _shown = true;
                _border.gameObject.SetActive(true);
            }
        }

        public void Hide()
        {
            if (!_shown) return;

            _shown = false;
            _disc.gameObject.SetActive(false);
            _border.gameObject.SetActive(false);
        }

        private void Build()
        {
            _built = true;

            var source = Resources.Load<Material>("WalkArea");
            if (source == null)
            {
                Debug.LogWarning("[Mascot] XR cursor: could not load the WalkArea material, not drawing the cursor");
                _buildFailed = true;
                return;
            }

            _fillMaterial = new Material(source) { renderQueue = FillRenderQueue };
            _fillMaterial.SetColor("_BaseColor", FillColor);
            _borderMaterial = new Material(source) { renderQueue = BorderRenderQueue };
            _borderMaterial.SetColor("_BaseColor", BorderColor);

            _disc = BuildPart("Disc", 0f, _fillMaterial);
            _border = BuildPart("Border", BorderInnerRadius, _borderMaterial);
            _disc.gameObject.SetActive(false);
            _border.gameObject.SetActive(false);
        }

        private Transform BuildPart(string name, float innerRadius, Material material)
        {
            var go = XrWalkAreaView.BuildFlat(name, XrWalkAreaView.BuildRingMesh(innerRadius, 1f), material);
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        private void OnDestroy()
        {
            if (_fillMaterial != null) Destroy(_fillMaterial);
            if (_borderMaterial != null) Destroy(_borderMaterial);
        }
    }
}
