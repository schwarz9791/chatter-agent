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
        private const int HandCount = 2;
        private const int LeftHand = 0;

        private const string HandTrackingPermission = "android.permission.HAND_TRACKING";
        private const string SceneUnderstandingCoarsePermission = "android.permission.SCENE_UNDERSTANDING_COARSE";

        /// <summary>追跡を短く見失っても、つまみを離したと判定しない猶予（秒）。</summary>
        private const float TrackingGraceSeconds = 0.2f;

        private XROrigin _origin;
        private VrmStage _stage;

        private bool _scenePermissionGranted;

        private ARPlaneManager _planeManager;

        // ★ Hand Interaction Profile（OpenXR）のバインド。手の関節ではなく、aim レイと
        //   pinchValue だけを読む —— 奥行きの操作は考えず、Mac のドラッグに相当する
        //   操作にするため（つまんだレイの先に追従、離したら平面へ）。
        private readonly InputAction[] _pointerPosition =
        {
            new InputAction(binding: "<HandInteraction>{LeftHand}/pointerPosition"),
            new InputAction(binding: "<HandInteraction>{RightHand}/pointerPosition"),
        };

        private readonly InputAction[] _pointerRotation =
        {
            new InputAction(binding: "<HandInteraction>{LeftHand}/pointerRotation"),
            new InputAction(binding: "<HandInteraction>{RightHand}/pointerRotation"),
        };

        private readonly InputAction[] _pinchValue =
        {
            new InputAction(binding: "<HandInteraction>{LeftHand}/pinchValue"),
            new InputAction(binding: "<HandInteraction>{RightHand}/pinchValue"),
        };

        private readonly InputAction[] _isTracked =
        {
            new InputAction(binding: "<HandInteraction>{LeftHand}/isTracked"),
            new InputAction(binding: "<HandInteraction>{RightHand}/isTracked"),
        };

        private readonly bool[] _pinching = new bool[HandCount];
        private readonly float[] _lastTrackedAt = new float[HandCount];

        /// <summary>掴んでいる手の index。掴んでいなければ null。</summary>
        private int? _grabbedHand;

        /// <summary>掴んだ瞬間の、aim レイに沿ったヒット距離。</summary>
        private float _grabDistance;

        /// <summary>掴んだ瞬間の、レイ上の点から <c>ModelAnchor</c> へのオフセット。</summary>
        private Vector3 _grabOffset;

        /// <summary>レイ上の現在の点（<c>ModelAnchor</c> ではなく、掴んでいる点そのもの）。離すときの吸着に使う。</summary>
        private Vector3 _held;

        public void Begin(XROrigin origin, VrmStage stage)
        {
            _origin = origin;
            _stage = stage;
            for (var hand = 0; hand < HandCount; hand++)
            {
                _pointerPosition[hand].Enable();
                _pointerRotation[hand].Enable();
                _pinchValue[hand].Enable();
                _isTracked[hand].Enable();
            }
        }

        private void OnDestroy()
        {
            for (var hand = 0; hand < HandCount; hand++)
            {
                _pointerPosition[hand].Dispose();
                _pointerRotation[hand].Dispose();
                _pinchValue[hand].Dispose();
                _isTracked[hand].Dispose();
            }
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
            for (var hand = 0; hand < HandCount; hand++)
            {
                UpdateHand(hand, offset);
            }
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

        private void UpdateHand(int hand, Transform offset)
        {
            if (_isTracked[hand].controls.Count == 0 || _isTracked[hand].ReadValue<float>() < 0.5f)
            {
                if (_grabbedHand == hand && Time.unscaledTime - _lastTrackedAt[hand] > TrackingGraceSeconds)
                {
                    Release();
                }
                return;
            }

            _lastTrackedAt[hand] = Time.unscaledTime;

            var wasPinching = _pinching[hand];
            var pinchValue = _pinchValue[hand].controls.Count > 0 ? _pinchValue[hand].ReadValue<float>() : 0f;
            _pinching[hand] = XrGrabRules.IsPinching(wasPinching, pinchValue);

            if (_grabbedHand == null)
            {
                // ★ つまみに「入った瞬間」だけを契機にする。つまんだままレイを動かして
                //   キャラに当てても掴まない（意図しない掴みを防ぐ）。
                if (!wasPinching && _pinching[hand]) TryGrab(hand, offset);
                return;
            }

            if (_grabbedHand != hand) return;

            if (_pinching[hand]) UpdateHeld(hand, offset);
            else Release();
        }

        private void TryGrab(int hand, Transform offset)
        {
            if (_pointerPosition[hand].controls.Count == 0) return;

            var ray = ReadAimRay(hand, offset);

            // ★ 直前のフレームでキャラを動かしていると、Raycast がそれを見ない
            //   （autoSyncTransforms はオフ）。
            Physics.SyncTransforms();
            var collider = _stage.Model.GetComponentInChildren<Collider>();
            if (collider == null) return;
            if (!collider.Raycast(ray, out var hit, float.PositiveInfinity)) return;

            _grabbedHand = hand;
            _grabDistance = hit.distance;
            _grabOffset = _stage.ModelAnchor.position - hit.point;
            _held = hit.point;
            Debug.Log($"[Mascot] XR grab: 掴みました hand={(hand == LeftHand ? "left" : "right")}");
        }

        private void UpdateHeld(int hand, Transform offset)
        {
            var ray = ReadAimRay(hand, offset);
            _held = ray.GetPoint(_grabDistance);
            _stage.ModelAnchor.position = _held + _grabOffset;
        }

        private Ray ReadAimRay(int hand, Transform offset)
        {
            var position = offset.TransformPoint(_pointerPosition[hand].ReadValue<Vector3>());
            var rotation = _pointerRotation[hand].ReadValue<Quaternion>();
            return new Ray(position, offset.TransformDirection(rotation * Vector3.forward));
        }

        private void Release()
        {
            _grabbedHand = null;

            var anchor = _stage.ModelAnchor;
            var landed = "none";
            if (_planeManager != null && TryFindGroundHeight(anchor.position, _held.y, out var groundY))
            {
                anchor.position = new Vector3(anchor.position.x, groundY, anchor.position.z);
                landed = groundY.ToString("F2");
            }

            var faced = XrGrabRules.TryYawToFace(anchor.position, _origin.Camera.transform.position, out var yaw);
            if (faced) anchor.rotation = Quaternion.Euler(0f, yaw, 0f);

            Debug.Log($"[Mascot] XR grab: 離しました plane={landed} yaw={(faced ? yaw.ToString("F1") : "unchanged")}");
        }

        /// <summary>
        /// <paramref name="feetWorld"/> の xz・<paramref name="heldHeightWorld"/> の高さから
        /// 真下へ探し、最も近い水平面の高さ（ワールド）を返す。無ければ false。
        ///
        /// ★ <b>足元の高さから探さない。</b> 頭を持って下ろすと足元は天板より下に潜るので、
        ///   足元から探すと体の下にある面ではなく床が先に見つかって落ちる。掴んでいた点は
        ///   キャラの体の上にあるので、その高さから探せば体の下にある面が取れる。
        /// ★ <c>InverseTransformRay</c> は使わない。core-utils と ARFoundation の拡張が
        ///   衝突する（CS0121）ので、<c>InverseTransformPoint</c> / <c>InverseTransformDirection</c>
        ///   で組む。
        /// </summary>
        private bool TryFindGroundHeight(Vector3 feetWorld, float heldHeightWorld, out float groundY)
        {
            groundY = 0f;

            var trackablesParent = _origin.TrackablesParent;
            var worldOrigin = new Vector3(feetWorld.x, heldHeightWorld, feetWorld.z);
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
    }
}
