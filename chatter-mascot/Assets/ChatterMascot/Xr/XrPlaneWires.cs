using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 検知した平面の輪郭を線で描くだけの係。<b>状態は表示の ON/OFF だけ</b>。
    ///
    /// ★ 描く形は着地の判定が見ている形そのものなので、<b>何も間引かない</b>
    ///   （<c>ARPlaneManager.Raycast</c> は trackables の全部を対象にする）。
    /// ★ <c>ARPlaneMeshVisualizer</c> は使わない。表示の条件に <c>ARSession.state</c> があるが、
    ///   このプロジェクトは <c>ARSession</c> を置いていないので線が常に消える。多角形の分割に
    ///   失敗すると線も更新しない。
    /// ★ 色を明るくしているのは、グラスが加算合成で暗い色が透けるため。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class XrPlaneWires : MonoBehaviour
    {
        private const float LineWidthMeters = 0.005f;

        private static readonly Color WireColor = new Color(0.3f, 1f, 1f, 1f);

        private ARPlaneManager _manager;
        private Material _material;
        private bool _visible;
        private bool _built;
        private bool _buildFailed;

        /// <summary>平面検知が動いている（<c>ARPlaneManager</c> が見つかっている）か。</summary>
        public bool Available
        {
            get
            {
                FindManager();
                return _manager != null;
            }
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (visible) return;
            FindManager();
            if (_manager == null) return;
            foreach (var plane in _manager.trackables)
            {
                if (plane.TryGetComponent<LineRenderer>(out var line)) line.enabled = false;
            }
        }

        private void Update()
        {
            if (!_visible) return;
            FindManager();
            if (_manager == null) return;
            if (!_built) Build();
            if (_buildFailed) return;

            // ponytail: ON の間は毎フレーム書き直す。重くなったら ARPlane.boundaryChanged へ
            foreach (var plane in _manager.trackables)
            {
                if (!plane.TryGetComponent<LineRenderer>(out var line)) line = Setup(plane.gameObject);
                var boundary = plane.boundary;
                line.positionCount = boundary.Length;
                for (var i = 0; i < boundary.Length; i++)
                {
                    var p = boundary[i];
                    line.SetPosition(i, new Vector3(p.x, 0f, p.y));
                }
                line.enabled = true;
            }
        }

        private void FindManager()
        {
            if (_manager == null) TryGetComponent(out _manager);
        }

        private LineRenderer Setup(GameObject go)
        {
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = true;
            line.widthMultiplier = LineWidthMeters;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = _material;
            return line;
        }

        private void Build()
        {
            _built = true;

            var source = Resources.Load<Material>("WalkArea");
            if (source == null)
            {
                Debug.LogWarning("[Mascot] XR planes: could not load the WalkArea material, not drawing the planes");
                _buildFailed = true;
                return;
            }

            _material = new Material(source);
            _material.SetColor("_BaseColor", WireColor);
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
