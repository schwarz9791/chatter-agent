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
    /// 手でつまんで動かし、離したら平面へ置き直す。掴む対象はモデル本体と、歩行範囲の円の
    /// ハンドル（<see cref="XrWalk"/>）の2つ —— aim レイ・pinchValue のヒステリシス・掴みの
    /// 排他はここに一本化したまま、先に設定パネル・歯車、次にハンドル、最後にモデルの順で見る。
    /// どれにも当たらなかったつまみは、歩行範囲の中を指した「行き先の指示」として扱う。
    ///
    /// ★ <b>手の入力を増やすのではなく、掴む対象を増やす。</b> 設定パネル・歯車の当たり判定も
    ///   ここが読んだレイを <see cref="XrSettingsBridge"/> へ渡すだけで、別の入力経路は作らない。
    ///
    /// ★ <b>シーンに置かない。</b> <see cref="XrStage"/> が配置（<see cref="XrPlacement"/>）の
    ///   直後に <c>AddComponent</c> で生やす —— 配置前のアンカーを掴ませないため。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class XrGrab : MonoBehaviour
    {
        /// <summary>internal: <see cref="XrHandTracking"/> も手のひらメニュー用の subsystem を
        /// 起動してよいかの判定にこの権限を読む——権限の要求自体はここが起動時にまとめて行う。</summary>
        internal const string HandTrackingPermission = "android.permission.HAND_TRACKING";
        private const string SceneUnderstandingCoarsePermission = "android.permission.SCENE_UNDERSTANDING_COARSE";

        /// <summary>追跡を短く見失っても、つまみを離したと判定しない猶予（秒）。</summary>
        private const float TrackingGraceSeconds = 0.2f;

        /// <summary>足元からこの割合（当たり判定の高さに対して）までは、体ではなく床を指したとみなす。</summary>
        private const float FeetHeightFraction = 0.1f;

        /// <summary>つまんでいる間、アンカーを行き先へ寄せる速さ（1/秒）。大きいほど遅れが小さい。</summary>
        private const float HeldFollowRate = 20f;

        private XROrigin _origin;
        private VrmStage _stage;
        private XrWalk _walk;
        private XrSettingsBridge _settings;

        private bool _scenePermissionGranted;

        private ARPlaneManager _planeManager;

        /// <summary>
        /// 並びの後ろほど優先する（マウス＞右手＞左手。簡単で説明できる決め打ち）。<see cref="TryGetAimRay"/>
        /// が読む代表レイと、<see cref="Update"/> が手を回す順（＝設定パネルのホバーの先勝ち）は
        /// どちらもこの並びを後ろから読むことで揃える。
        /// </summary>
        private readonly Hand[] _hands =
        {
            new Hand("LeftHand", "<HandInteraction>{LeftHand}", "pointerPosition", "pointerRotation", "pinchValue", "isTracked"),
            new Hand("RightHand", "<HandInteraction>{RightHand}", "pointerPosition", "pointerRotation", "pinchValue", "isTracked"),
            // ★ Android Mouse Interaction Profile（OpenXR）。click を pinchValue と同じ 0/1 として読み、
            //   ヒステリシス（XrGrabRules.IsPinching）をそのまま流用する
            new Hand("Mouse", "<AndroidMouseInteraction>", "aim/position", "aim/rotation", "click", "aim/isTracked"),
        };

        /// <summary>掴んでいる手。掴んでいなければ null。</summary>
        private Hand _grabbedHand;

        /// <summary>掴んでいるのがハンドルか（false ならモデル本体）。</summary>
        private bool _grabbedHandle;

        /// <summary>面を指していないときの奥行きのフォールバックに使う距離。面を指している間は
        /// 指した点までの距離で更新し続け、指さなくなった瞬間の値をそのまま引き継ぐ。</summary>
        private float _heldDistance;

        /// <summary>水平面を一度も指していない間に、掴んだ点を沿わせる高さ（ワールド）。</summary>
        private float _heldHeight;

        /// <summary>つまんでいる間の <c>ModelAnchor</c> の行き先。アンカーはここへ寄せて追う。</summary>
        private Vector3 _heldTarget;

        /// <summary>掴んだ瞬間の、レイ上の点から <c>ModelAnchor</c> へのオフセット。</summary>
        private Vector3 _grabOffset;

        /// <summary>掴んだ瞬間に指した点と足元の水平方向のずれ。指した点が動いた距離ぶんだけ
        /// ゼロへ縮め、動かさずに離せば足元は掴んだ位置に留まる。</summary>
        private Vector3 _slip;

        /// <summary>最後に指した水平面上の点。<see cref="_slip"/> を縮める基準と、面を外れた後に
        /// 足元を沿わせる高さにする。</summary>
        private Vector3 _lastPlanePoint;

        /// <summary>掴んでから水平面を一度でも指したか。</summary>
        private bool _hasLastPlanePoint;

        /// <summary>起動時に配置した直後の <c>ModelAnchor</c> の位置・向き。<see cref="ResetPosition"/> の行き先。</summary>
        private Vector3 _initialAnchorPosition;
        private Quaternion _initialAnchorRotation;

        public void Begin(XROrigin origin, VrmStage stage, XrWalk walk, XrSettingsBridge settings)
        {
            _origin = origin;
            _stage = stage;
            _walk = walk;
            _settings = settings;
            // ★ ここまで ModelAnchor を動かすものは無い（起動時の配置は XR Origin 側を動かすだけ）
            //   ので、いまの位置・向きがそのまま「起動時の位置」になる
            _initialAnchorPosition = stage.ModelAnchor.position;
            _initialAnchorRotation = stage.ModelAnchor.rotation;
            foreach (var hand in _hands) hand.Enable();
        }

        /// <summary>
        /// 「位置をリセット」。<c>ModelAnchor</c> を起動時と同じ位置・向きへ戻し、歩行範囲も
        /// 未配置・既定半径に戻す。つまんでいる最中なら、着地の判定はせずそのまま外す。
        /// </summary>
        public void ResetPosition()
        {
            if (_grabbedHand != null)
            {
                // ★ 掴んでいた対象に応じて外す。歩行範囲のハンドルを外し損ねると、
                //   「掴んでいる間は円を消さない」判定が真のまま残ってしまう
                if (_grabbedHandle) _walk?.ReleaseHandle();
                else _walk?.SetModelGrabbed(false);
                _grabbedHand = null;
            }

            var anchor = _stage.ModelAnchor;
            anchor.SetPositionAndRotation(_initialAnchorPosition, _initialAnchorRotation);
            // ★ 瞬間移動なので、揺れものが移動を慣性として拾わないよう戻す（Release と同じ理由）
            _stage.ResetSpringBones();
            _walk?.ResetArea();

            Debug.Log("[Mascot] XR grab: 位置をリセットしました");
        }

        private void OnDestroy()
        {
            foreach (var hand in _hands) hand.Dispose();
        }

        /// <summary>
        /// 追跡されている手の aim レイ。<see cref="ChatterMascot.Xr.XrCursorGazeSource"/> が
        /// 「目で追う」の入力に使う——<b>入力の読み取りはここに一本化したまま</b>、新しく
        /// Input System のバインドを増やさない。
        ///
        /// ★ 優先順は <see cref="_hands"/> の doc 参照。どれも追跡されていなければ <c>false</c>。
        /// </summary>
        public bool TryGetAimRay(out Ray ray)
        {
            ray = default;
            if (_origin == null) return false;

            var offset = _origin.CameraFloorOffsetObject.transform;
            for (var i = _hands.Length - 1; i >= 0; i--)
            {
                var hand = _hands[i];
                if (!hand.IsTracked.IsPressed()) continue;

                ray = ReadAimRay(hand, offset);
                return true;
            }

            return false;
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

            // ★ 優先順は _hands の doc 参照。先勝ちのホバー（XrSettingsBridge.UpdateHover）が
            //   同じ順になるよう、優先の高い手から回す
            var offset = _origin.CameraFloorOffsetObject.transform;
            for (var i = _hands.Length - 1; i >= 0; i--) UpdateHand(_hands[i], offset);
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

            // ★ つまんでいなくても毎フレーム渡す。設定パネルが開いていればホバーの表示に、
            //   閉じていれば歯車を出し続けるかの判定に使う（追跡されている手だけ）
            if (_settings != null) _settings.UpdateHover(ReadAimRay(hand, offset));

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

            // ★ 設定パネル・歯車を先に見る。当たっていればそちらを押して、
            //   キャラ・歩行範囲の掴みには進まない
            if (_settings != null && _settings.TryHandlePinch(ray)) return;

            if (_walk != null && _walk.TryGrabHandle(ray))
            {
                _grabbedHand = hand;
                _grabbedHandle = true;
                Debug.Log($"[Mascot] XR grab: 歩行範囲のハンドルを掴みました hand={hand.Name}");
                return;
            }

            var collider = SyncedModelCollider();
            // ★ ハンドルにもキャラクターにも当たらないつまみは、歩行範囲の中を指した「行き先の指示」
            if (collider == null || !collider.Raycast(ray, out var hit, float.PositiveInfinity))
            {
                // ★ 円を出すのは検知した平面を指したときだけ。床の無限平面はわずかに下向きの
                //   レイもほとんど「範囲外」にするので、判定にだけ使い円は出さない。
                if (_walk != null && _walk.TryWalkTo(ray) == WalkToResult.OutOfRange &&
                    TryRaycastHorizontalPlane(ray, XrGrabRules.MaxHeldDistance, out _))
                {
                    _walk.ShowAreaBriefly();
                }

                return;
            }

            // ★ 足元に当たったつまみは、歩ける範囲の中なら行き先の指示を優先する。当たり判定は
            //   足先まで包むので、そのままだと足元の近くを指しても体を掴んで置き直しになる
            var bounds = collider.bounds;
            if (hit.point.y < _stage.ModelAnchor.position.y + bounds.size.y * FeetHeightFraction &&
                _walk != null && _walk.TryWalkTo(ray) == WalkToResult.Started)
            {
                return;
            }

            _grabbedHand = hand;
            _grabbedHandle = false;
            _heldDistance = hit.distance;
            _grabOffset = _stage.ModelAnchor.position - hit.point;
            _heldHeight = hit.point.y;
            _heldTarget = _stage.ModelAnchor.position;

            // ★ 掴んだ瞬間に指した点と足元のずれを覚えておく。つまんだ瞬間はレイの先が体の奥へ
            //   抜けやすく、そのまま足元を合わせると動かしていないのに位置がずれる。
            if (TryRaycastHorizontalPlane(ray, XrGrabRules.MaxHeldDistance, out var grabPlanePoint))
            {
                _slip = _stage.ModelAnchor.position - grabPlanePoint;
                _slip.y = 0f;
                _heldDistance = Vector3.Distance(ray.origin, grabPlanePoint);
                _lastPlanePoint = grabPlanePoint;
                _hasLastPlanePoint = true;
            }
            else
            {
                _slip = Vector3.zero;
                _hasLastPlanePoint = false;
            }

            _walk?.SetModelGrabbed(true);
            if (_settings != null) _settings.ShowGearForAWhile();
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
            if (_grabbedHandle)
            {
                _walk.DragHandle(ReadAimRay(hand, offset));
                return;
            }

            var ray = ReadAimRay(hand, offset);

            // ★ 水平面を指していれば、指した点に足元を置く（掴んだ瞬間のずれは指した点が
            //   動くぶんだけ縮める）。面を外れた後も、最後に指した面の高さとレイの交点に足元を
            //   置く —— 置き方がそろうので、面の縁や距離の上限で目標が跳ばない。
            //   面を一度も指していない間だけ、掴んだ点をレイに沿わせる。
            if (TryRaycastHorizontalPlane(ray, XrGrabRules.MaxHeldDistance, out var planePoint))
            {
                _heldDistance = Vector3.Distance(ray.origin, planePoint);

                if (_hasLastPlanePoint)
                {
                    _slip = Vector3.MoveTowards(_slip, Vector3.zero, Vector3.Distance(planePoint, _lastPlanePoint));
                }

                _lastPlanePoint = planePoint;
                _hasLastPlanePoint = true;
                _heldTarget = planePoint + _slip;
            }
            else if (_hasLastPlanePoint)
            {
                var distance = XrGrabRules.HeldDistance(ray, _lastPlanePoint.y, _heldDistance);
                _heldTarget = ray.GetPoint(distance) + _slip;
            }
            else
            {
                var distance = XrGrabRules.HeldDistance(ray, _heldHeight, _heldDistance);
                _heldTarget = ray.GetPoint(distance) + _grabOffset;
            }

            // ★ 指す面が切り替わると目標が跳ぶので、寄せて追う
            var anchor = _stage.ModelAnchor;
            anchor.position = Vector3.Lerp(anchor.position, _heldTarget,
                1f - Mathf.Exp(-HeldFollowRate * Time.unscaledDeltaTime));
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

            if (_grabbedHandle)
            {
                _walk?.ReleaseHandle();
                Debug.Log("[Mascot] XR grab: 歩行範囲のハンドルを離しました");
                return;
            }

            _walk?.SetModelGrabbed(false);

            // ★ 寄せている途中で離したら、追いつき先に置く。指した所に着かせるため
            var anchor = _stage.ModelAnchor;
            anchor.position = _heldTarget;
            var collider = SyncedModelCollider();
            var landed = "none";
            float? groundY = null;
            if (_planeManager != null && collider != null &&
                TryFindGroundHeight(anchor.position, collider.bounds.max.y, out var foundGroundY))
            {
                groundY = foundGroundY;
                anchor.position = new Vector3(anchor.position.x, foundGroundY, anchor.position.z);
                landed = foundGroundY.ToString("F2");
            }

            var faced = XrGrabRules.TryYawToFace(anchor.position, _origin.Camera.transform.position, out var yaw);
            if (faced) anchor.rotation = Quaternion.Euler(0f, yaw, 0f);

            // ★ 平面への落下と向け直しは瞬間移動なので、揺れものが移動を慣性として拾わないよう戻す。
            _stage.ResetSpringBones();

            // ★ 平面が見つかったときだけ歩行範囲を出す。無ければ Unplace で歩行を止め、
            //   円を消す —— 次の XrWalk.Update が古い中心へアンカーを引き戻すのを防ぐ。
            if (groundY.HasValue)
            {
                _walk?.PlaceAt(anchor.position, groundY.Value, faced ? yaw : anchor.rotation.eulerAngles.y);
            }
            else
            {
                _walk?.Unplace();
            }

            if (_settings != null) _settings.ShowGearForAWhile();
            Debug.Log($"[Mascot] XR grab: 離しました plane={landed} yaw={(faced ? yaw.ToString("F1") : "unchanged")}");
        }

        /// <summary>
        /// <paramref name="feetWorld"/> の xz・<paramref name="searchFromYWorld"/> の高さから
        /// 真下へ探し、最も近い水平面の高さ（ワールド）を返す。無ければ false。
        ///
        /// ★ <b>足元の高さから探さない。</b> 足元は下ろすと天板に潜り、掴んだ点も足元の近くだと
        ///   天板より下になる。当たり判定の上端から探せば、どこをつまんでいても体の下にある面が取れる。
        /// </summary>
        private bool TryFindGroundHeight(Vector3 feetWorld, float searchFromYWorld, out float groundY)
        {
            var worldOrigin = new Vector3(feetWorld.x, searchFromYWorld, feetWorld.z);
            var found = TryRaycastHorizontalPlane(new Ray(worldOrigin, Vector3.down), float.PositiveInfinity, out var point);
            groundY = point.y;
            return found;
        }

        /// <summary>
        /// ワールドのレイが最初に当たる上向きの水平面上の点（ワールド）。<paramref name="maxDistance"/>
        /// （ワールド距離）より遠い当たりは無視する。平面検知が動いていない・当たらなければ false。
        ///
        /// ★ <c>InverseTransformRay</c> は使わない。core-utils と ARFoundation の拡張が
        ///   衝突する（CS0121）ので、<c>InverseTransformPoint</c> / <c>InverseTransformDirection</c>
        ///   で組む。
        /// ★ <c>ARPlaneManager.Raycast</c> は面を下からも当てる。<c>HorizontalUp</c> は上から見た
        ///   面が前提なので、上向き・水平のレイは無効として弾く。
        /// ★ <c>hit.distance</c> はトラッカブル空間のローカル距離。<paramref name="maxDistance"/> は
        ///   ワールド距離なので、当たった点をワールドへ戻してから比べる。
        /// </summary>
        private bool TryRaycastHorizontalPlane(Ray worldRay, float maxDistance, out Vector3 point)
        {
            point = default;
            if (_planeManager == null) return false;
            if (worldRay.direction.y >= 0f) return false;

            var trackablesParent = _origin.TrackablesParent;
            var localRay = new Ray(
                trackablesParent.InverseTransformPoint(worldRay.origin),
                trackablesParent.InverseTransformDirection(worldRay.direction));

            using var hits = _planeManager.Raycast(localRay, TrackableType.PlaneWithinPolygon, Allocator.Temp);
            var found = false;
            var closestDistance = float.MaxValue;
            foreach (var hit in hits)
            {
                var plane = _planeManager.GetPlane(hit.trackableId);
                if (plane == null || plane.alignment != PlaneAlignment.HorizontalUp) continue;

                var worldPoint = trackablesParent.TransformPoint(hit.pose.position);
                var worldDistance = Vector3.Distance(worldRay.origin, worldPoint);
                if (worldDistance > maxDistance || worldDistance >= closestDistance) continue;

                closestDistance = worldDistance;
                point = worldPoint;
                found = true;
            }

            return found;
        }

        /// <summary>
        /// 片手（またはマウス）ぶんの入力と、つまみの状態。
        ///
        /// ★ Hand Interaction Profile / Android Mouse Interaction Profile（OpenXR）のバインド。
        ///   手の関節ではなく、aim レイとつまみ相当の値だけを読む —— Mac のドラッグに相当する
        ///   操作にするため（つまんだレイの先に追従、離したら平面へ）。マウスは click を
        ///   pinchValue と同じ 0/1 として読み、つまみのヒステリシスをそのまま流用する。
        ///   奥行きは、水平面を指していればその点まで、指していなければ掴んだ距離とレイの
        ///   俯角から決める（<see cref="XrGrabRules.HeldDistance"/>）。
        /// ★ バインドが解決していない（デバイスが無い）ときは、どのアクションも既定値を返し
        ///   <c>IsPressed()</c> は false になるので、追跡していない手として扱われる。
        /// </summary>
        private sealed class Hand
        {
            /// <summary>ログに出す名前（<c>LeftHand</c> / <c>RightHand</c> / <c>Mouse</c>）。</summary>
            public readonly string Name;

            public readonly InputAction PointerPosition;
            public readonly InputAction PointerRotation;
            public readonly InputAction PinchValue;
            public readonly InputAction IsTracked;

            public bool Pinching;
            public float LastTrackedAt;

            /// <param name="devicePath">バインドのデバイス部分（<c>&lt;HandInteraction&gt;{LeftHand}</c> など）。</param>
            /// <param name="positionControl">aim 位置のコントロール名。</param>
            /// <param name="rotationControl">aim 向きのコントロール名。</param>
            /// <param name="pinchControl">つまみ相当（0..1）のコントロール名。</param>
            /// <param name="isTrackedControl">追跡状態のコントロール名。</param>
            public Hand(string name, string devicePath, string positionControl, string rotationControl,
                string pinchControl, string isTrackedControl)
            {
                Name = name;
                PointerPosition = Bind(devicePath, positionControl);
                PointerRotation = Bind(devicePath, rotationControl);
                PinchValue = Bind(devicePath, pinchControl);
                IsTracked = Bind(devicePath, isTrackedControl);
            }

            private static InputAction Bind(string devicePath, string control) =>
                new InputAction(binding: $"{devicePath}/{control}");

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
