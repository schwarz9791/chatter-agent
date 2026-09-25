using UnityEngine;
using UnityEngine.Rendering;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 歩行範囲の円とハンドルを描くだけの係。<b>状態を持たない。</b> <see cref="Sync"/> が渡す値を
    /// そのまま反映する（状態は <c>XrWalk</c> が持つ）。
    ///
    /// ★ <b>ワールドに置く。<c>ModelAnchor</c> の子にしない</b> —— 縮尺が掛かるうえキャラと一緒に
    ///   動いてしまう。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class XrWalkAreaView : MonoBehaviour
    {
        private const float FloorClearanceMeters = 0.002f;
        private const float HandleRadiusMeters = 0.015f;

        /// <summary>不透明度が 0↔1 を渡るのにかける秒数。出現・消滅の両方に使う。</summary>
        private const float FadeSeconds = 0.5f;

        /// <summary>ハンドルを円の塗りより少し浮かせる（同じ高さだと面が食い合う）。</summary>
        private const float HandleLiftMeters = 0.001f;
        private const int RingSegments = 48;
        private const float RimInnerRadius = 0.96f;

        /// <summary>ハンドルの当たり判定を、見た目の円より何倍大きく取るか。</summary>
        private const float HandleGrabMargin = 2f;

        private static readonly Color FillColor = new Color(0.35f, 0.45f, 0.85f, 0.45f);
        private static readonly Color RimColor = new Color(0.15f, 0.25f, 0.85f, 1f);

        private Transform _fill;
        private Transform _rim;
        private Transform _handle;
        private Material _fillMaterial;
        private Material _rimMaterial;

        private bool _built;
        private bool _buildFailed;

        /// <summary>不透明度。0（消えている）〜1（出ている）。<see cref="Sync"/> でだけ動かす。</summary>
        private float _opacity;

        /// <summary>ハンドルのつまみ判定。ビューを組めなかったときは <c>null</c>。</summary>
        public Collider HandleCollider { get; private set; }

        /// <summary>
        /// 円・フチ・ハンドルを、渡された値どおりに置く。<paramref name="handleAngleDegrees"/> は
        /// キャラクターのヨーと同じ約束（ローカル −Z が正面）で測ったハンドルの方角。
        /// <paramref name="radius"/> は既にキャラクターの大きさぶん伸び縮みさせた実寸（円はこれで
        /// 拡大縮尺する）。<paramref name="scale"/> はハンドルの見た目と当たり判定だけの倍率——
        /// 歯車と同じ「押しやすさ」の倍率で、円の拡大縮尺とは別に伸び縮みさせる（大きいキャラほど
        /// 的も大きくする）。
        ///
        /// ★ <paramref name="visible"/> は即座の on/off ではなく、不透明度を <see cref="FadeSeconds"/>
        ///   かけて 0↔1 へ動かす目標。<b>消える側の呼び出しでも毎フレーム呼び続けること</b>
        ///   ——呼ばれなくなるとフェードが途中で止まって固まる。
        /// </summary>
        public void Sync(Vector3 center, float floorY, float radius, float handleAngleDegrees, float scale, bool visible)
        {
            if (!_built) Build();
            if (_buildFailed) return;

            transform.position = new Vector3(center.x, floorY + FloorClearanceMeters, center.z);
            _fill.localScale = new Vector3(radius, 1f, radius);
            _rim.localScale = new Vector3(radius, 1f, radius);

            // ★ ハンドルの向きはキャラクターの向きと同じ約束で測る（ローカル −Z が正面）。
            //   呼び出し側がキャラのヨーにそのまま角度を足して渡せる
            _handle.localPosition = Quaternion.Euler(0f, handleAngleDegrees, 0f) * Vector3.back * radius
                                    + Vector3.up * HandleLiftMeters;
            // ★ SphereCollider.radius はローカル単位なので、見た目と一緒に localScale で伸ばせば
            //   当たり判定も追従する（別々に大きさを持たせない）
            _handle.localScale = Vector3.one * HandleRadiusMeters * scale;

            var target = visible ? 1f : 0f;
            var opacity = Mathf.MoveTowards(_opacity, target, Time.unscaledDeltaTime / FadeSeconds);
            if (opacity != _opacity)
            {
                _opacity = opacity;
                _fillMaterial.SetColor("_BaseColor", new Color(FillColor.r, FillColor.g, FillColor.b, FillColor.a * _opacity));
                _rimMaterial.SetColor("_BaseColor", new Color(RimColor.r, RimColor.g, RimColor.b, RimColor.a * _opacity));
            }

            var active = _opacity > 0f;
            _fill.gameObject.SetActive(active);
            _rim.gameObject.SetActive(active);
            _handle.gameObject.SetActive(active);
        }

        private void Build()
        {
            _built = true;

            var source = Resources.Load<Material>("WalkArea");
            if (source == null)
            {
                Debug.LogWarning("[Mascot] XR walk: WalkArea マテリアルを読めないので歩行範囲を描きません");
                _buildFailed = true;
                return;
            }

            _fillMaterial = new Material(source);
            _fillMaterial.SetColor("_BaseColor", FillColor);
            _rimMaterial = new Material(source);
            _rimMaterial.SetColor("_BaseColor", RimColor);
            // ★ フチとハンドルは不透明にし、塗りより後に描く。半透明どうしだと重なりの枚数と
            //   描画順で色が変わり、同じマテリアルでも同じ色に見えない
            _rimMaterial.renderQueue = _fillMaterial.renderQueue + 1;

            _fill = BuildRing("Fill", 0f, 1f, _fillMaterial).transform;
            _rim = BuildRing("Rim", RimInnerRadius, 1f, _rimMaterial).transform;
            _handle = BuildHandle().transform;
        }

        private GameObject BuildRing(string name, float innerRadius, float outerRadius, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);

            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildRingMesh(innerRadius, outerRadius);

            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            return go;
        }

        /// <summary>
        /// ハンドル。<b>円と同じ平らな面で描く</b> —— 立体にすると裏面が重なって、同じ色でも
        /// フチより濃く見える。
        /// </summary>
        private GameObject BuildHandle()
        {
            var go = BuildRing("Handle", 0f, 1f, _rimMaterial);

            // ★ 当たり判定は見た目より大きく取る。腕を伸ばした先の数 cm の的を、
            //   手の震えぶんだけ外し続けることになる
            var collider = go.AddComponent<SphereCollider>();
            collider.radius = HandleGrabMargin;
            HandleCollider = collider;
            return go;
        }

        /// <summary>
        /// 単位半径の扇／環メッシュ（XZ 平面、法線 +Y）。<paramref name="innerRadius"/> が 0 なら扇（塗り）、
        /// それ以外なら環（フチ）——同じヘルパを使い回す。
        /// </summary>
        private static Mesh BuildRingMesh(float innerRadius, float outerRadius)
        {
            var vertexCount = (RingSegments + 1) * 2;
            var vertices = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var triangles = new int[RingSegments * 6];

            for (var i = 0; i <= RingSegments; i++)
            {
                var angle = i * Mathf.PI * 2f / RingSegments;
                var sin = Mathf.Sin(angle);
                var cos = Mathf.Cos(angle);
                vertices[i * 2] = new Vector3(sin * innerRadius, 0f, cos * innerRadius);
                vertices[i * 2 + 1] = new Vector3(sin * outerRadius, 0f, cos * outerRadius);
                normals[i * 2] = Vector3.up;
                normals[i * 2 + 1] = Vector3.up;
            }

            for (var i = 0; i < RingSegments; i++)
            {
                var v = i * 2;
                var t = i * 6;
                triangles[t + 0] = v;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 1;
                triangles[t + 4] = v + 3;
                triangles[t + 5] = v + 2;
            }

            return new Mesh { name = "WalkAreaRing", vertices = vertices, normals = normals, triangles = triangles };
        }

        private void OnDestroy()
        {
            if (_fillMaterial != null) Destroy(_fillMaterial);
            if (_rimMaterial != null) Destroy(_rimMaterial);
        }
    }
}
