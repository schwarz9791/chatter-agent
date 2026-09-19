using System.Collections.Generic;
using ChatterMascot.Vrm;
using Unity.Collections;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Android;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// 手でつまんで動かし、離したら平面へ置き直す。
    ///
    /// ★ <b>シーンに置かない。</b> <see cref="XrStage"/> が配置（<see cref="XrPlacement"/>）の
    ///   直後に <c>AddComponent</c> で生やす —— 配置前のアンカーを掴ませないため。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class XrGrab : MonoBehaviour
    {
        private const string HandTrackingPermission = "android.permission.HAND_TRACKING";
        private const string SceneUnderstandingCoarsePermission = "android.permission.SCENE_UNDERSTANDING_COARSE";

        /// <summary>追跡を短く見失っても、つまみを離したと判定しない猶予（秒）。</summary>
        private const float TrackingGraceSeconds = 0.2f;

        private XROrigin _origin;
        private VrmStage _stage;

        private bool _scenePermissionGranted;

        private ARPlaneManager _planeManager;

        private readonly Hand[] _hands = { new Hand("LeftHand"), new Hand("RightHand") };

        /// <summary>掴んでいる手。掴んでいなければ null。</summary>
        private Hand _grabbedHand;

        /// <summary>掴んだ瞬間の、aim レイに沿ったヒット距離。</summary>
        private float _grabDistance;

        /// <summary>掴んだ瞬間の、レイ上の点から <c>ModelAnchor</c> へのオフセット。</summary>
        private Vector3 _grabOffset;

        public void Begin(XROrigin origin, VrmStage stage)
        {
            _origin = origin;
            _stage = stage;
            foreach (var hand in _hands) hand.Enable();
        }

        private void OnDestroy()
        {
            foreach (var hand in _hands) hand.Dispose();
        }

        private void Start()
        {
            if (Application.platform != RuntimePlatform.Android) return;

            _scenePermissionGranted = Permission.HasUserAuthorizedPermission(SceneUnderstandingCoarsePermission);

            // ★ 許可済みのものまで RequestUserPermissions に含めると、権限 Activity が一瞬起動して
            //   アプリが pause/resume する。未許可のものだけまとめて要求する。
            var missing = new List<string>(2);
            if (!Permission.HasUserAuthorizedPermission(HandTrackingPermission)) missing.Add(HandTrackingPermission);
            if (!_scenePermissionGranted) missing.Add(SceneUnderstandingCoarsePermission);
            if (missing.Count == 0) return;

            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += OnPermissionGranted;
            callbacks.PermissionDenied += OnPermissionDenied;
            Permission.RequestUserPermissions(missing.ToArray(), callbacks);
        }

        // ★ コールバックがどのスレッドで来るか保証が無いので、フラグを立てるだけにする。
        //   ARPlaneManager の生成は Update から行う。手の入力は、権限が無ければ値が来ないだけ
        //   なので、許可を待って何かを始める必要は無い。
        private void OnPermissionGranted(string permission)
        {
            if (permission == SceneUnderstandingCoarsePermission) _scenePermissionGranted = true;
        }

        private void OnPermissionDenied(string permission)
        {
            Debug.LogWarning($"[Mascot] XR grab: {permission} が拒否されました");
        }

        private void Update()
        {
            EnsurePlaneManager();

            if (_origin == null || _stage == null || _stage.Model == null) return;

            var offset = _origin.CameraFloorOffsetObject.transform;
            foreach (var hand in _hands) UpdateHand(hand, offset);
        }

        private void EnsurePlaneManager()
        {
            if (!_scenePermissionGranted || _planeManager != null || _origin == null) return;

            // ★ ARPlaneManager は RequireComponent(XROrigin) なので、必ず XR Origin の
            //   GameObject に付ける（別の GO に付けると XROrigin が勝手に生える）
            _planeManager = _origin.gameObject.AddComponent<ARPlaneManager>();
            _planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            Debug.Log("[Mascot] XR grab: 平面検知を開始しました");
        }

        private void UpdateHand(Hand hand, Transform offset)
        {
            if (!hand.IsTracked.IsPressed())
            {
                if (_grabbedHand == hand && Time.unscaledTime - hand.LastTrackedAt > TrackingGraceSeconds)
                {
                    Release();
                }

                // ★ 追跡が外れている間のつまみは分からないので、戻ったときのつまみを入りとして
                //   扱う。掴んでいる手は猶予の間だけ状態を保つ（抜けの閾値のまま戻れるように）。
                if (_grabbedHand != hand) hand.Pinching = false;
                return;
            }

            hand.LastTrackedAt = Time.unscaledTime;

            var wasPinching = hand.Pinching;
            hand.Pinching = XrGrabRules.IsPinching(wasPinching, hand.PinchValue.ReadValue<float>());

            if (_grabbedHand == null)
            {
                // ★ つまみに「入った瞬間」だけを契機にする。つまんだままレイを動かして
                //   キャラに当てても掴まない（意図しない掴みを防ぐ）。
                if (!wasPinching && hand.Pinching) TryGrab(hand, offset);
                return;
            }

            if (_grabbedHand != hand) return;

            if (hand.Pinching) UpdateHeld(hand, offset);
            else Release();
        }

        private void TryGrab(Hand hand, Transform offset)
        {
            var ray = ReadAimRay(hand, offset);

            var collider = SyncedModelCollider();
            if (collider == null) return;
            if (!collider.Raycast(ray, out var hit, float.PositiveInfinity)) return;

            _grabbedHand = hand;
            _grabDistance = hit.distance;
            _grabOffset = _stage.ModelAnchor.position - hit.point;
            Debug.Log($"[Mascot] XR grab: 掴みました hand={hand.Name}");
        }

        /// <summary>
        /// キャラクターの当たり判定を取り直す。
        ///
        /// ★ 直前のフレームでキャラを動かしていると、Raycast / bounds がそれを見ない
        ///   （autoSyncTransforms はオフ）。
        /// </summary>
        private Collider SyncedModelCollider()
        {
            Physics.SyncTransforms();
            return _stage.Model.GetComponentInChildren<Collider>();
        }

        private void UpdateHeld(Hand hand, Transform offset)
        {
            _stage.ModelAnchor.position = ReadAimRay(hand, offset).GetPoint(_grabDistance) + _grabOffset;
        }

        private static Ray ReadAimRay(Hand hand, Transform offset)
        {
            var position = offset.TransformPoint(hand.PointerPosition.ReadValue<Vector3>());
            var rotation = hand.PointerRotation.ReadValue<Quaternion>();
            return new Ray(position, offset.TransformDirection(rotation * Vector3.forward));
        }

        private void Release()
        {
            _grabbedHand = null;

            var anchor = _stage.ModelAnchor;
            var collider = SyncedModelCollider();
            var landed = "none";
            if (_planeManager != null && collider != null &&
                TryFindGroundHeight(anchor.position, collider.bounds.max.y, out var groundY))
            {
                anchor.position = new Vector3(anchor.position.x, groundY, anchor.position.z);
                landed = groundY.ToString("F2");
            }

            var faced = XrGrabRules.TryYawToFace(anchor.position, _origin.Camera.transform.position, out var yaw);
            if (faced) anchor.rotation = Quaternion.Euler(0f, yaw, 0f);

            // ★ 平面への落下と向け直しは瞬間移動なので、揺れものが移動を慣性として拾わないよう戻す。
            _stage.ResetSpringBones();

            Debug.Log($"[Mascot] XR grab: 離しました plane={landed} yaw={(faced ? yaw.ToString("F1") : "unchanged")}");
        }

        /// <summary>
        /// <paramref name="feetWorld"/> の xz・<paramref name="searchFromYWorld"/> の高さから
        /// 真下へ探し、最も近い水平面の高さ（ワールド）を返す。無ければ false。
        ///
        /// ★ <b>足元の高さから探さない。</b> 足元は下ろすと天板に潜り、掴んだ点も足元の近くだと
        ///   天板より下になる。当たり判定の上端から探せば、どこをつまんでいても体の下にある面が取れる。
        /// ★ <c>InverseTransformRay</c> は使わない。core-utils と ARFoundation の拡張が
        ///   衝突する（CS0121）ので、<c>InverseTransformPoint</c> / <c>InverseTransformDirection</c>
        ///   で組む。
        /// </summary>
        private bool TryFindGroundHeight(Vector3 feetWorld, float searchFromYWorld, out float groundY)
        {
            groundY = 0f;

            var trackablesParent = _origin.TrackablesParent;
            var worldOrigin = new Vector3(feetWorld.x, searchFromYWorld, feetWorld.z);
            var localRay = new Ray(
                trackablesParent.InverseTransformPoint(worldOrigin),
                trackablesParent.InverseTransformDirection(Vector3.down));

            using var hits = _planeManager.Raycast(localRay, TrackableType.PlaneWithinPolygon, Allocator.Temp);
            var found = false;
            var closestDistance = float.MaxValue;
            foreach (var hit in hits)
            {
                var plane = _planeManager.GetPlane(hit.trackableId);
                if (plane == null || plane.alignment != PlaneAlignment.HorizontalUp) continue;
                if (hit.distance >= closestDistance) continue;

                closestDistance = hit.distance;
                groundY = trackablesParent.TransformPoint(hit.pose.position).y;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// 片手ぶんの入力と、つまみの状態。
        ///
        /// ★ Hand Interaction Profile（OpenXR）のバインド。手の関節ではなく、aim レイと
        ///   pinchValue だけを読む —— 奥行きの操作は考えず、Mac のドラッグに相当する
        ///   操作にするため（つまんだレイの先に追従、離したら平面へ）。
        /// ★ バインドが解決していない（手の入力が無い）ときは、どのアクションも既定値を返し
        ///   <c>IsPressed()</c> は false になるので、追跡していない手として扱われる。
        /// </summary>
        private sealed class Hand
        {
            /// <summary>バインドの usage 名（<c>LeftHand</c> / <c>RightHand</c>）。</summary>
            public readonly string Name;

            public readonly InputAction PointerPosition;
            public readonly InputAction PointerRotation;
            public readonly InputAction PinchValue;
            public readonly InputAction IsTracked;

            public bool Pinching;
            public float LastTrackedAt;

            public Hand(string name)
            {
                Name = name;
                PointerPosition = Bind("pointerPosition");
                PointerRotation = Bind("pointerRotation");
                PinchValue = Bind("pinchValue");
                IsTracked = Bind("isTracked");
            }

            private InputAction Bind(string control) =>
                new InputAction(binding: $"<HandInteraction>{{{Name}}}/{control}");

            public void Enable()
            {
                PointerPosition.Enable();
                PointerRotation.Enable();
                PinchValue.Enable();
                IsTracked.Enable();
            }

            public void Dispose()
            {
                PointerPosition.Dispose();
                PointerRotation.Dispose();
                PinchValue.Dispose();
                IsTracked.Dispose();
            }
        }
    }
}
