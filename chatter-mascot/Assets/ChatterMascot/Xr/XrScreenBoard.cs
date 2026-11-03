using ChatterMascot.Vrm;
using Unity.XR.CoreUtils;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections;
using ChatterMascot.Net;
using ChatterMascot.Ui;
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using Unity.XR.CompositionLayers.Services;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.CompositionLayers;
#endif

namespace ChatterMascot.Xr
{
    /// <summary>
    /// Mac の画面を、キャラクターの奥に固定した板（Composition Layer）へ映す。
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

        private XROrigin _origin;
        private VrmStage _stage;
        private MascotRunner _runner;

        // 板の位置と幅は最初に出すときに決め、以後のセッションでも変えない
        private bool _hasPose;
        private Vector3 _position;
        private Quaternion _rotation;
        private float _width;

        private readonly WaitForSeconds _poll = new WaitForSeconds(PollSeconds);

        public void Begin(XROrigin origin, VrmStage stage)
        {
            _origin = origin;
            _stage = stage;
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

        private bool _focused = true;
        private bool _retryNow;
        private CompositionLayer _layer;
        private int _layerId;
        private bool _hasLayerId;
        private GameObject _punch;

        private void OnApplicationFocus(bool hasFocus)
        {
            _focused = hasFocus;
            if (hasFocus)
            {
                _retryNow = true;
                return;
            }
            Receiver("stop");
            DestroyBoard();
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
                DestroyBoard();

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
            if (!_hasPose)
            {
                var camera = _origin.Camera;
                if (camera == null || _stage == null || _stage.ModelAnchor == null) return false;
                (_position, _rotation) = XrBoardRules.InitialPose(
                    camera.transform.position, _stage.ModelAnchor.position, camera.transform.forward);
                _width = XrBoardRules.DefaultWidth;
                _hasPose = true;
            }

            var boardHeight = _width * height / width;

            // 構成し終えるまで有効にしない
            var go = new GameObject("ScreenBoardLayer");
            go.SetActive(false);
            var layer = go.AddComponent<CompositionLayer>();
            layer.ChangeLayerDataType<QuadLayerData>();
            ((QuadLayerData)layer.LayerData).Size = new Vector2(_width, boardHeight);
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
            _punch.transform.SetPositionAndRotation(_position, _rotation);
            _punch.transform.localScale = new Vector3(_width, boardHeight, 1f);
            _punch.GetComponent<Renderer>().sharedMaterial = new Material(Resources.Load<Shader>("ScreenPunch"));

            Debug.Log($"[Mascot] XR screen: board shown {width}x{height}");
            return true;
        }

        private void DestroyBoard()
        {
            if (_layer != null) Destroy(_layer.gameObject);
            _layer = null;
            _hasLayerId = false;
            if (_punch == null) return;
            Destroy(_punch.GetComponent<Renderer>().sharedMaterial);
            Destroy(_punch);
            _punch = null;
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
