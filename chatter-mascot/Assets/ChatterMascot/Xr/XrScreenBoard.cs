using Unity.XR.CoreUtils;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections;
using ChatterMascot.Net;
using ChatterMascot.Ui;
using Unity.Collections;
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using Unity.XR.CompositionLayers.Services;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.CompositionLayers;
using UnityEngine.XR.OpenXR.Features.Android;
#endif

namespace ChatterMascot.Xr
{
    /// <summary>
    /// Mac の画面を、部屋に固定した板（Composition Layer）へ映す。つまんで動かし、角で拡縮できる。
    /// 受信・復号は <c>ScreenReceiver.java</c>、契約は <c>docs/protocol.md</c>「画面の配信」。
    ///
    /// ★ 板は映像が届いている間だけ在る。アプリが前面を離れたら受信とレイヤーを必ず畳む
    ///   （Surface は戻ると作り直されるので、復帰は新しい接続として組み直す）。
    /// ★ 板とキャラクターの前後は、レイヤーの順序と穴あけの quad で合わせる。
    /// </summary>
    public sealed class XrScreenBoard : MonoBehaviour
    {
        private const string JavaClass = "tech.sukima.chattermascot.ScreenReceiver";
        private const string JniClass = "tech/sukima/chattermascot/ScreenReceiver";

        private const float PollSeconds = 0.1f;
        private const float SurfacePollSeconds = 0.5f;
        private const float RetrySeconds = 5f;
        private const float ApprovalToastSeconds = 5f;

        /// <summary>部屋を透かしたまま要求した値が届くのを待つ秒数。</summary>
        private const float DimmingDirectSeconds = 0.25f;

        /// <summary>OPAQUE にして値が届くのを待つ上限の秒数。</summary>
        private const float DimmingHoldSeconds = 0.5f;

        /// <summary>OPAQUE から戻した後、値が落ち着くのを待つ秒数。</summary>
        private const float DimmingSettleSeconds = 0.5f;

        /// <summary>角のハンドルを、板か角を指さなくなってからも出しておく秒数。</summary>
        private const float HandleLingerSeconds = 3f;

        private const float HandleRadiusMeters = 0.02f;

        /// <summary>ハンドルを穴あけの板より手前へ浮かせる（同じ深さだと競る）。</summary>
        private const float HandleLiftMeters = 0.005f;

        private XROrigin _origin;
        private MascotRunner _runner;

        // 板の位置は最初に出すときに決め、以後のセッションでも保つ。長辺は位置を決め直しても保つ
        private Vector3 _position;
        private Quaternion _rotation;
        private float _longSide = XrBoardRules.DefaultLongSide;

        // 映像の縦横比（高さ / 幅）。繋ぎ直しで変わりうる
        private float _aspect = 1f;

        private Vector2 Size => XrBoardRules.Size(_longSide, _aspect);

        private float _handlesUntil;
        private bool _handlesShown = false;
        private BoardPart _dragPart;
        private bool _hasDragBase;
        private BoardDrag _drag;
        private Vector3 _grabPoint;
        private float _longSideAtGrab;

        private readonly WaitForSeconds _poll = new WaitForSeconds(PollSeconds);

        public void Begin(XROrigin origin)
        {
            _origin = origin;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!OpenXRRuntime.IsExtensionEnabled("XR_KHR_android_surface_swapchain"))
            {
                Debug.Log("[Mascot] XR screen: XR_KHR_android_surface_swapchain is not enabled, screen board disabled");
                enabled = false;
                return;
            }
            _focused = Application.isFocused;
            StartCoroutine(Run());
#else
            enabled = false;
#endif
        }

        /// <summary>板が出ているか。エディタでは常に false。</summary>
        public bool Shown
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return _layer != null;
#else
                return false;
#endif
            }
        }

        /// <summary>この端末で部屋を暗くできるか。板が使える状態かには依らない。エディタでは常に false。</summary>
        public bool DimmingSupported
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (!_dimmingSupported.HasValue) _dimmingSupported = ProbeDimming();
                return _dimmingSupported.Value;
#else
                return false;
#endif
            }
        }

        /// <summary>減光（0〜1）。板が出ていない間は <paramref name="normal"/>、出ている間は <paramref name="sharing"/> を、その場で効かせる。</summary>
        public void SetDimming(float normal, float sharing)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _normalDimming = normal;
            _sharingDimming = sharing;
            ApplyDimming(Shown ? sharing : normal);
#endif
        }

        /// <summary>
        /// レイが板のどこに当たるか。見えていない角は掴ませない
        /// （同じフレームの <see cref="Hover"/> で出す前の、実際に見えていた状態で決める）。
        /// </summary>
        public BoardPart Pick(Ray ray, out float distance)
        {
            distance = 0f;
            if (!Shown) return BoardPart.None;
            return XrBoardRules.Pick(ray, _position, _rotation, Size, _handlesShown, out distance);
        }

        /// <summary>板か角を指している間、ハンドルを出し続ける。</summary>
        public void Hover(Ray ray)
        {
            if (!Shown) return;
            if (XrBoardRules.Pick(ray, _position, _rotation, Size, true, out _) != BoardPart.None) ShowHandles();
        }

        private void ShowHandles()
        {
            _handlesUntil = Time.unscaledTime + HandleLingerSeconds;
        }

        public void BeginDrag(BoardPart part)
        {
            _dragPart = part;
            _hasDragBase = false;
        }

        /// <summary>
        /// 基準は、レイが板や球に交わった最初のフレームで取り、そのフレームでは動かさない。
        /// 以後、交わらないフレームは最後の姿勢を保つ。
        /// </summary>
        public void Drag(Ray ray)
        {
            if (!Shown || _dragPart == BoardPart.None || _origin.Camera == null) return;
            ShowHandles();
            var head = _origin.Camera.transform.position;

            if (_dragPart == BoardPart.Body)
            {
                if (!_hasDragBase)
                {
                    _hasDragBase = BoardDrag.TryBegin(ray, head, _position, out _drag);
                    return;
                }
                if (!_drag.TryMove(ray, head, out var position, out var rotation)) return;
                _position = position;
                _rotation = rotation;
            }
            else
            {
                if (!XrBoardRules.TryPlanePoint(ray, _position, _rotation, out var point)) return;
                if (!_hasDragBase)
                {
                    _grabPoint = point;
                    _longSideAtGrab = _longSide;
                    _hasDragBase = true;
                    return;
                }
                _longSide = XrBoardRules.ResizedLongSide(_longSideAtGrab, _position, _grabPoint, point);
            }
            Apply();
        }

        public void EndDrag()
        {
            if (_dragPart == BoardPart.None) return;
            _dragPart = BoardPart.None;
            LogPose("drag ended");
        }

        /// <summary>
        /// 位置を今の視点の正面へ戻す。大きさは保つ。出ていなければ、次に出すときに決め直す。
        /// </summary>
        public void ResetPosition()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _dragPart = BoardPart.None;
            if (!Shown || _origin.Camera == null)
            {
                _hasPose = false;
                return;
            }
            var camera = _origin.Camera.transform;
            (_position, _rotation) = XrBoardRules.InitialPose(camera.position, camera.forward);
            Apply();
            LogPose("position reset");
#endif
        }

#if !(UNITY_ANDROID && !UNITY_EDITOR)
        private static void Apply() { }
#endif

        private void LogPose(string what)
        {
            var camera = _origin.Camera;
            if (camera == null) return;
            var toBoard = _position - camera.transform.position;
            Debug.Log($"[Mascot] XR screen: {what} distance={toBoard.magnitude:F3} " +
                      $"elevation={XrBoardRules.Elevation(toBoard):F1} size={Size.x:F3}x{Size.y:F3}");
        }

        /// <summary>覚えている Mac の証明書の指紋を忘れる（Mac を入れ替えたときに繋ぎ直せるようにする）。</summary>
        public static void ForgetMac()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            _certToasted = false;
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var java = new AndroidJavaClass(JavaClass))
                {
                    java.CallStatic("forgetMac", activity);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] XR screen: could not forget the Mac: " + e.Message);
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool _certToasted;

        private bool _hasPose;
        private float _normalDimming;
        private float _sharingDimming;
        private bool? _dimmingSupported;
        private float[] _dimmingLevels = Array.Empty<float>();
        private float _dimmingTarget;
        private bool _dimmingRunning;
        private bool _focused = true;
        private bool _retryNow;
        private CompositionLayer _layer;
        private int _layerId;
        private bool _hasLayerId;
        private GameObject _punch;
        private readonly GameObject[] _handles = new GameObject[4];
        private Material _handleMaterial;
        private Mesh _handleMesh;

        private void OnApplicationFocus(bool hasFocus)
        {
            _focused = hasFocus;
            if (hasFocus)
            {
                // 離れている間に値が保たれる保証は無いので、戻ってきたら今の状態の値を要求し直す
                ApplyDimming(Shown ? _sharingDimming : _normalDimming);
                _retryNow = true;
                return;
            }
            Receiver("stop");
            HideBoard();
        }

        private void OnDestroy()
        {
            Receiver("stop");
            DestroyBoard();
        }

        private IEnumerator Run()
        {
            while (true)
            {
                // 接続先は発見や再ペアリングで、通知なしに変わる。試行のたびに読み直す
                while (!_focused || !Connected()) yield return _poll;

                _retryNow = false;
                if (ServerUrl.ToScreenEndpoint(_runner.ServerUrl, out var host, out var port))
                    yield return Session(host, port);

                Receiver("stop");
                HideBoard();

                // ponytail: 共有の開始を知らせる経路が無いので、一定間隔で繋ぎに行く。
                // 上げるなら mDNS の広告か発話側のフレームで開始を知らせる
                var waited = 0f;
                while (waited < RetrySeconds && !_retryNow)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
            }
        }

        private bool Connected()
        {
            if (_runner == null) _runner = FindFirstObjectByType<MascotRunner>();
            return _runner != null && _runner.ConnectedOnce;
        }

        private IEnumerator Session(string host, int port)
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var java = new AndroidJavaClass(JavaClass))
            {
                java.CallStatic("start", activity, host, port);
            }

            var nextApprovalToastAt = 0f;
            var attached = false;
            var nextSurfaceAt = 0f;
            while (_focused)
            {
                yield return _poll;

                var state = Receiver<int>("state");
                if (state == ScreenState.Idle) yield break;
                if (state == ScreenState.CertMismatch)
                {
                    if (!_certToasted)
                    {
                        _certToasted = true;
                        DeviceToast.Show(UiText.For(Application.systemLanguage).XrScreenCertChanged);
                    }
                    yield break;
                }

                // トーストは数秒で消えるが Mac のダイアログは答えるまで残るので、いつでも比べられるように出し直す
                if (state == ScreenState.WaitingApproval && Time.unscaledTime >= nextApprovalToastAt)
                {
                    nextApprovalToastAt = Time.unscaledTime + ApprovalToastSeconds;
                    DeviceToast.Show(UiText.For(Application.systemLanguage).XrScreenApprove(Receiver<string>("confirmCode")));
                }

                if (state != ScreenState.Ready || attached || Time.unscaledTime < nextSurfaceAt) continue;

                if (_layer == null)
                {
                    // 作った直後は Surface がまだ無く、取りに行くとエラーログが出る
                    if (BuildBoard(Receiver<int>("width"), Receiver<int>("height")))
                        nextSurfaceAt = Time.unscaledTime + SurfacePollSeconds;
                    continue;
                }
                if (!_hasLayerId) _hasLayerId = CompositionLayerManager.TryGetLayerId(_layer, out _layerId);
                if (!_hasLayerId) continue;

                // 取れるまで呼ぶとエラーログが出るので、間隔を空ける
                var surface = OpenXRLayerUtility.GetLayerAndroidSurfaceObject(_layerId);
                if (surface == IntPtr.Zero)
                {
                    nextSurfaceAt = Time.unscaledTime + SurfacePollSeconds;
                    continue;
                }
                AttachSurface(surface);
                attached = true;
            }
        }

        private bool BuildBoard(int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            _aspect = (float)height / width;
            if (!_hasPose)
            {
                var camera = _origin.Camera;
                if (camera == null) return false;
                (_position, _rotation) = XrBoardRules.InitialPose(camera.transform.position, camera.transform.forward);
                _hasPose = true;
            }

            // 構成し終えるまで有効にしない
            var go = new GameObject("ScreenBoardLayer");
            go.SetActive(false);
            var layer = go.AddComponent<CompositionLayer>();
            layer.ChangeLayerDataType<QuadLayerData>();
            ((QuadLayerData)layer.LayerData).Size = Size;
            var textures = go.AddComponent<TexturesExtension>();
            textures.sourceTexture = TexturesExtension.SourceTextureEnum.AndroidSurface;
            textures.Resolution = new Vector2(width, height);
            textures.CropToAspect = false;
            layer.Order = -1;
            go.transform.SetPositionAndRotation(_position, _rotation);
            go.SetActive(true);
            _layer = layer;
            _hasLayerId = false;

            _punch = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _punch.name = "ScreenBoardPunch";
            Destroy(_punch.GetComponent<Collider>());
            _punch.GetComponent<Renderer>().sharedMaterial = new Material(Resources.Load<Shader>("ScreenPunch"));

            BuildHandles();
            Apply();
            ApplyDimming(_sharingDimming);

            Debug.Log($"[Mascot] XR screen: board shown {width}x{height}");
            LogPose("board placed");
            return true;
        }

        private void BuildHandles()
        {
            var source = Resources.Load<Material>("WalkArea");
            if (source == null)
            {
                Debug.LogWarning("[Mascot] XR screen: could not load the WalkArea material, not drawing the corner handles");
                return;
            }
            _handleMaterial = new Material(source);
            _handleMaterial.SetColor("_BaseColor", XrWalkAreaView.RimColor);
            _handleMesh = XrWalkAreaView.BuildRingMesh(0f, 1f);
            for (var i = 0; i < _handles.Length; i++)
            {
                var handle = XrWalkAreaView.BuildFlat("ScreenBoardHandle" + i, _handleMesh, _handleMaterial);
                handle.SetActive(false);
                _handles[i] = handle;
            }
            _handlesShown = false;
        }

        // 板の姿勢と大きさの反映先は、ここ一箇所にそろえる
        private void Apply()
        {
            if (_layer == null) return;
            var size = Size;
            _layer.transform.SetPositionAndRotation(_position, _rotation);
            ((QuadLayerData)_layer.LayerData).Size = size;

            if (_punch != null)
            {
                _punch.transform.SetPositionAndRotation(_position, _rotation);
                _punch.transform.localScale = new Vector3(size.x, size.y, 1f);
            }

            var handleRotation = _rotation * Quaternion.Euler(90f, 0f, 0f);
            for (var i = 0; i < _handles.Length; i++)
            {
                if (_handles[i] == null) continue;
                var sx = (i & 1) == 0 ? -1f : 1f;
                var sy = (i & 2) == 0 ? -1f : 1f;
                var local = new Vector3(sx * size.x * 0.5f, sy * size.y * 0.5f, -HandleLiftMeters);
                _handles[i].transform.SetPositionAndRotation(_position + _rotation * local, handleRotation);
                _handles[i].transform.localScale = Vector3.one * HandleRadiusMeters;
            }
        }

        // 手ごとの呼び出しの空振りで消えないよう、見せるかどうかはここで一度だけ決める
        private void LateUpdate()
        {
            var visible = _layer != null && Time.unscaledTime < _handlesUntil;
            if (visible == _handlesShown) return;
            _handlesShown = visible;
            foreach (var handle in _handles)
            {
                if (handle != null) handle.SetActive(visible);
            }
        }

        private void HideBoard()
        {
            if (_layer != null) ApplyDimming(_normalDimming);
            DestroyBoard();
        }

        private void DestroyBoard()
        {
            if (_layer != null) Destroy(_layer.gameObject);
            _layer = null;
            _hasLayerId = false;
            _dragPart = BoardPart.None;
            _handlesUntil = 0f;
            _handlesShown = false;
            for (var i = 0; i < _handles.Length; i++)
            {
                if (_handles[i] != null) Destroy(_handles[i]);
                _handles[i] = null;
            }
            if (_handleMaterial != null) Destroy(_handleMaterial);
            if (_handleMesh != null) Destroy(_handleMesh);
            _handleMaterial = null;
            _handleMesh = null;
            if (_punch == null) return;
            Destroy(_punch.GetComponent<Renderer>().sharedMaterial);
            Destroy(_punch);
            _punch = null;
        }

        private bool ProbeDimming()
        {
            var supported = false;
            try
            {
                supported = OpenXRRuntime.IsExtensionEnabled(XrSeeThroughBlendFeature.DimmingExtension)
                    && !ARCameraFeature.TryGetGlobalDimmingLevel().status.IsError();
                if (supported)
                {
                    var levels = ARCameraFeature.TryGetSupportedDimmingLevels(Allocator.Temp);
                    if (!levels.status.IsError())
                    {
                        _dimmingLevels = levels.value.ToArray();
                        levels.value.Dispose();
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] XR screen: could not check dimming support: " + e.Message);
            }
            Debug.Log($"[Mascot] XR screen: dimming supported={supported} levels={string.Join(", ", Array.ConvertAll(_dimmingLevels, l => l.ToString("F4")))}");
            return supported;
        }

        private void ApplyDimming(float level)
        {
            if (!DimmingSupported) return;
            _dimmingTarget = level;
            if (_dimmingRunning) return;
            _dimmingRunning = true;
            StartCoroutine(ApplyDimmingRun());
        }

        // ランタイムによっては、部屋を透かすモードの間は減光の要求を無視する。OPAQUE の間に届けた値は、戻しても残る。
        // OPAQUE の途中で値を変えると届かないことがあるので、次の値は、今の値を届けて戻してから要求する
        private IEnumerator ApplyDimmingRun()
        {
            var level = _dimmingTarget;
            var held = false;
            var retried = false;
            RequestDimming(level);
            yield return WaitForDimming(level, DimmingDirectSeconds);

            // 戻した後にも値は変わる（システムが上書きする）。点滅を繰り返さないよう、掛け直しは 1 回だけ
            while (level == _dimmingTarget)
            {
                if (!DimmingReached(level, out _))
                {
                    held = true;
                    XrSeeThroughBlendFeature.HoldOpaque(true);
                    yield return WaitForDimming(level, DimmingHoldSeconds);
                    XrSeeThroughBlendFeature.HoldOpaque(false);
                }
                yield return new WaitForSecondsRealtime(DimmingSettleSeconds);
                if (retried || DimmingReached(level, out _)) break;
                retried = true;
            }

            DimmingReached(level, out var current);
            Debug.Log($"[Mascot] XR screen: dimming requested={level:F2} current={current:F2} heldOpaque={held} retried={retried}");
            _dimmingRunning = level != _dimmingTarget;
            if (_dimmingRunning) StartCoroutine(ApplyDimmingRun());
        }

        private IEnumerator WaitForDimming(float level, float seconds)
        {
            var deadline = Time.unscaledTime + seconds;
            while (Time.unscaledTime < deadline && !DimmingReached(level, out _)) yield return null;
        }

        private static void RequestDimming(float level)
        {
            // セッションを畳む途中では例外になりうる。減光の失敗で後始末を止めない
            try
            {
                var status = ARCameraFeature.TryRequestGlobalDimmingLevel(level);
                if (status.IsError()) Debug.LogWarning($"[Mascot] XR screen: dimming request failed: {status.statusCode}");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] XR screen: dimming request failed: " + e.Message);
            }
        }

        private bool DimmingReached(float level, out float current)
        {
            current = 0f;
            try
            {
                var result = ARCameraFeature.TryGetGlobalDimmingLevel();
                if (result.status.IsError()) return false;
                current = result.value;
                return XrBoardRules.DimmingReached(current, level, _dimmingLevels);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] XR screen: could not read the dimming level: " + e.Message);
                return false;
            }
        }

        private static void AttachSurface(IntPtr surface)
        {
            var cls = AndroidJNI.FindClass(JniClass);
            if (cls == IntPtr.Zero)
            {
                Debug.LogError("[Mascot] XR screen: ScreenReceiver class not found");
                return;
            }
            try
            {
                var method = AndroidJNI.GetStaticMethodID(cls, "attachSurface", "(Ljava/lang/Object;)V");
                var args = new jvalue[1];
                args[0].l = surface;
                AndroidJNI.CallStaticVoidMethod(cls, method, args);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(cls);
            }
        }

        private static void Receiver(string method)
        {
            using (var java = new AndroidJavaClass(JavaClass)) java.CallStatic(method);
        }

        private static T Receiver<T>(string method)
        {
            using (var java = new AndroidJavaClass(JavaClass)) return java.CallStatic<T>(method);
        }

        /// <summary><c>ScreenReceiver</c> の状態定数。</summary>
        private static class ScreenState
        {
            public const int Idle = 0;
            public const int WaitingApproval = 2;
            public const int Ready = 3;
            public const int CertMismatch = 5;
        }
#endif
    }
}
