using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 着地できる面を、矩形の塗りと輪郭で描くだけの係。<b>状態は表示の ON/OFF だけ</b>。
    ///
    /// ★ 描く面と範囲は着地の判定と同じにする（<see cref="XrGrab.CanLandOn"/> と、
    ///   <see cref="ARPlane.extents"/> の矩形）。矩形の中が、そのまま着地できる範囲になる。
    /// ★ <c>ARPlaneMeshVisualizer</c> は使わない。表示の条件に <c>ARSession.state</c> があるが、
    ///   このプロジェクトは <c>ARSession</c> を置いていないので線が常に消える。
    /// ★ グラスは加算合成で、暗い色ほど透けて薄く見える。塗りは輪郭より暗くして、天板を覆い隠さない。
    /// ★ 表示している間は、すべての平面の値を定期的にログへ出す。見えている形と、ランタイムが返した
    ///   値を突き合わせるため。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class XrPlaneWires : MonoBehaviour
    {
        private const float LineWidthMeters = 0.005f;
        private const float LogIntervalSeconds = 1f;
        private const string FillName = "PlaneFill";

        private static readonly Color WireColor = new Color(0.15f, 0.45f, 0.65f, 0.4f);
        private static readonly Color FillColor = new Color(0.08f, 0.2f, 0.55f, 0.08f);

        private ARPlaneManager _manager;
        private Material _wireMaterial;
        private Material _fillMaterial;
        private Mesh _quad;
        private readonly HashSet<Vector3Int> _drawnCenters = new HashSet<Vector3Int>();
        private bool _visible;
        private bool _built;
        private bool _buildFailed;
        private float _nextLogAt;

        /// <summary>平面検知を始めている（<c>ARPlaneManager</c> が見つかっている）か。</summary>
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
                if (plane.TryGetComponent<LineRenderer>(out var line)) Show(plane.transform, line, false);
            }
        }

        private void Update()
        {
            if (!_visible) return;
            FindManager();
            if (_manager == null) return;
            if (!_built) Build();
            if (_buildFailed) return;

            var log = Time.unscaledTime >= _nextLogAt;
            if (log)
            {
                _nextLogAt = Time.unscaledTime + LogIntervalSeconds;
                Debug.Log($"[Mascot] XR planes: count={_manager.trackables.count}");
            }

            // ponytail: ON の間は毎フレーム書き直す。重くなったら ARPlaneManager.trackablesChanged へ
            _drawnCenters.Clear();
            foreach (var plane in _manager.trackables)
            {
                if (log) LogPlane(plane);
                if (!XrGrab.CanLandOn(plane)) continue;

                // ★ ランタイムは同じ面を別の id で何枚も返すことがある。重ねて塗ると不透明になって
                //   モデルを隠すので、中心が同じ面は 1 枚だけ描く
                var first = _drawnCenters.Add(Vector3Int.RoundToInt(plane.center * 100f));
                var hasLine = plane.TryGetComponent<LineRenderer>(out var line);
                if (!first)
                {
                    if (hasLine) Show(plane.transform, line, false);
                    continue;
                }

                if (!hasLine) line = Setup(plane.transform);
                var fill = plane.transform.Find(FillName);

                var c = plane.centerInPlaneSpace;
                var e = plane.extents;
                const float y = -XrGrab.PlaneHeightBiasMeters;
                line.SetPosition(0, new Vector3(c.x - e.x, y, c.y - e.y));
                line.SetPosition(1, new Vector3(c.x + e.x, y, c.y - e.y));
                line.SetPosition(2, new Vector3(c.x + e.x, y, c.y + e.y));
                line.SetPosition(3, new Vector3(c.x - e.x, y, c.y + e.y));
                fill.localPosition = new Vector3(c.x, y, c.y);
                fill.localScale = new Vector3(e.x * 2f, 1f, e.y * 2f);
                Show(plane.transform, line, true);
            }
        }

        private static void Show(Transform plane, LineRenderer line, bool shown)
        {
            line.enabled = shown;
            var fill = plane.Find(FillName);
            if (fill != null) fill.GetComponent<MeshRenderer>().enabled = shown;
        }

        private static void LogPlane(ARPlane plane)
        {
            var center = plane.center;
            var extents = plane.extents;
            Debug.Log($"[Mascot] XR planes: id={plane.trackableId} y={center.y:F3} " +
                      $"center=({center.x:F2},{center.z:F2}) extents=({extents.x:F2},{extents.y:F2}) " +
                      $"verts={plane.boundary.Length} align={plane.alignment} class={plane.classifications} " +
                      $"tracking={plane.trackingState}");
        }

        private void FindManager()
        {
            if (_manager == null) TryGetComponent(out _manager);
        }

        private LineRenderer Setup(Transform plane)
        {
            var line = plane.gameObject.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.loop = true;
            line.positionCount = 4;
            line.widthMultiplier = LineWidthMeters;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = _wireMaterial;

            var fill = XrWalkAreaView.BuildFlat(FillName, _quad, _fillMaterial);
            fill.transform.SetParent(plane, false);
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

            _fillMaterial = new Material(source);
            _fillMaterial.SetColor("_BaseColor", FillColor);
            _wireMaterial = new Material(source);
            _wireMaterial.SetColor("_BaseColor", WireColor);
            // ★ 塗り → 輪郭 → 歩行範囲の順に描く。半透明どうしは描く順で重なりの色が変わる
            _fillMaterial.renderQueue = source.renderQueue - 2;
            _wireMaterial.renderQueue = source.renderQueue - 1;

            _quad = new Mesh
            {
                name = "PlaneFill",
                vertices = new[]
                {
                    new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                    new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f),
                },
                normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
            };
        }

        private void OnDestroy()
        {
            if (_wireMaterial != null) Destroy(_wireMaterial);
            if (_fillMaterial != null) Destroy(_fillMaterial);
            if (_quad != null) Destroy(_quad);
        }
    }
}
