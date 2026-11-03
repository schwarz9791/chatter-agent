using System;
using System.Collections;
using System.IO;
using ChatterMascot.Vrm;
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using Unity.XR.CompositionLayers.Services;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.CompositionLayers;

namespace ChatterMascot.XrSpike
{
    public static class SurfaceSpike
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bind()
        {
            if (XRGeneralSettings.Instance?.Manager?.activeLoader == null)
            {
                Debug.Log("[Spike] XR is not running, skipping");
                return;
            }
            var go = new GameObject("SurfaceSpike");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<SurfaceSpikeBehaviour>();
        }
    }

    [Serializable]
    public sealed class SpikeConfig
    {
        public string source;
        public string shape = "quad";
        public int order = -1;
        public float offsetForward = 0.5f;
        public float offsetRight = 0f;
        public float offsetUp = 0f;
        public float width = 1.2f;
        public int resW = 1920;
        public int resH = 1080;
        public bool punch;
    }

    public sealed class SurfaceSpikeBehaviour : MonoBehaviour
    {
        private const string AnchorLogMarker = "[Mascot] XR: spatial anchor";
        private const float AnchorWaitTimeoutSeconds = 20f;

        private SpikeConfig _config;
        private CompositionLayer _layer;
        private int _layerId;
        private IntPtr _surface;
        private bool _started;
        private volatile bool _anchored;

        private void Start()
        {
            _config = new SpikeConfig { source = Application.persistentDataPath + "/test.h264" };
            var path = Application.persistentDataPath + "/spike.json";
            if (File.Exists(path))
                JsonUtility.FromJsonOverwrite(File.ReadAllText(path), _config);
            else
                Debug.Log("[Spike] " + path + " not found, using defaults");
            Debug.Log("[Spike] config " + JsonUtility.ToJson(_config));
            Debug.Log("[Spike] XR_KHR_android_surface_swapchain enabled=" +
                      OpenXRRuntime.IsExtensionEnabled("XR_KHR_android_surface_swapchain"));

            StartCoroutine(Run());
            StartCoroutine(LogFps());
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            Debug.Log("[Spike] focus=" + hasFocus);
            if (!hasFocus || !_started) return;
            var surface = FetchSurface();
            if (surface == IntPtr.Zero || surface == _surface) return;
            Debug.Log("[Spike] surface changed " + _surface + " -> " + surface + ", restarting receiver");
            _surface = surface;
            StopReceiver();
            StartReceiver();
        }

        private void OnDestroy()
        {
            StopReceiver();
        }

        private IEnumerator Run()
        {
            // XrStage は配置を終えると "[Mascot] XR: spatial anchor" をログに出す。XrStage を触らずに完了を知るためそれを待つ
            Application.logMessageReceived += OnLog;
            var waited = 0f;
            while (!_anchored && waited < AnchorWaitTimeoutSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            Application.logMessageReceived -= OnLog;
            Debug.Log("[Spike] anchored=" + _anchored + " after " + waited.ToString("F1") + " s");
            yield return null;

            VrmStage stage = null;
            Camera cam = null;
            while (stage == null || stage.ModelAnchor == null || cam == null)
            {
                stage = FindFirstObjectByType<VrmStage>(FindObjectsInactive.Include);
                cam = Camera.main;
                yield return null;
            }

            BuildLayer(cam.transform.position, stage.ModelAnchor.position);

            var t0 = Time.realtimeSinceStartup;
            while (!CompositionLayerManager.TryGetLayerId(_layer, out _layerId))
                yield return null;
            Debug.Log("[Spike] layer id=" + _layerId + " order=" + _layer.Order);

            // 取得に失敗するたびにエラーログが出るので、ゆっくり引く
            while (true)
            {
                _surface = FetchSurface();
                if (_surface != IntPtr.Zero) break;
                yield return new WaitForSeconds(0.5f);
            }
            Debug.Log("[Spike] surface arrived " + (Time.realtimeSinceStartup - t0).ToString("F2") + " s after layer id");
            _started = true;
            StartReceiver();
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (message.StartsWith(AnchorLogMarker, StringComparison.Ordinal)) _anchored = true;
        }

        private void BuildLayer(Vector3 viewer, Vector3 character)
        {
            var toChar = character - viewer;
            toChar.y = 0f;
            if (toChar.sqrMagnitude < 1e-4f)
            {
                toChar = Camera.main.transform.forward;
                toChar.y = 0f;
            }
            var forward = toChar.normalized;
            var right = Vector3.Cross(Vector3.up, forward);

            // 目の高さを板の中心に合わせる
            var boardPos = character + forward * _config.offsetForward + right * _config.offsetRight;
            boardPos.y = viewer.y + _config.offsetUp;

            var look = boardPos - viewer;
            look.y = 0f;
            look = look.sqrMagnitude < 1e-4f ? forward : look.normalized;
            var rotation = Quaternion.LookRotation(look, Vector3.up);
            var height = _config.width * _config.resH / _config.resW;

            var go = new GameObject("SpikeVideoLayer");
            go.SetActive(false);

            var layer = go.AddComponent<CompositionLayer>();
            var position = boardPos;
            if (_config.shape == "cylinder")
            {
                // 円筒は中心が transform。見える弧は中心から look 方向に radius 離れた所にある
                var radius = Mathf.Max(0.3f, Vector3.Distance(new Vector3(viewer.x, 0f, viewer.z), new Vector3(boardPos.x, 0f, boardPos.z)));
                layer.ChangeLayerDataType<CylinderLayerData>();
                var data = (CylinderLayerData)layer.LayerData;
                data.Radius = radius;
                data.CentralAngle = Mathf.Min(_config.width / radius, 2f * Mathf.PI - 0.01f);
                data.AspectRatio = (float)_config.resW / _config.resH;
                position = boardPos - look * radius;
            }
            else
            {
                layer.ChangeLayerDataType<QuadLayerData>();
                var data = (QuadLayerData)layer.LayerData;
                data.Size = new Vector2(_config.width, height);
            }

            var tex = go.AddComponent<TexturesExtension>();
            tex.sourceTexture = TexturesExtension.SourceTextureEnum.AndroidSurface;
            tex.Resolution = new Vector2(_config.resW, _config.resH);
            tex.CropToAspect = false;
            layer.Order = _config.order;

            go.transform.SetPositionAndRotation(position, rotation);
            go.SetActive(true);

            if (_config.punch)
            {
                var hole = GameObject.CreatePrimitive(PrimitiveType.Quad);
                hole.name = "SpikePunch";
                Destroy(hole.GetComponent<Collider>());
                hole.transform.SetPositionAndRotation(boardPos, rotation);
                hole.transform.localScale = new Vector3(_config.width, height, 1f);
                hole.GetComponent<Renderer>().sharedMaterial = new Material(Resources.Load<Shader>("SpikePunch"));
            }

            Debug.Log("[Spike] board position=" + boardPos + " layer transform=" + position + " character=" + character + " viewer=" + viewer +
                      " shape=" + _config.shape + " size=" + _config.width.ToString("F2") + "x" + height.ToString("F2"));
            _layer = layer;
        }

        private IntPtr FetchSurface()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_layer == null || !CompositionLayerManager.TryGetLayerId(_layer, out var id)) return IntPtr.Zero;
            return OpenXRLayerUtility.GetLayerAndroidSurfaceObject(id);
#else
            return IntPtr.Zero;
#endif
        }

        private void StartReceiver()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var cls = AndroidJNI.FindClass("tech/sukima/chattermascot/SpikeVideoReceiver");
            if (cls == IntPtr.Zero)
            {
                Debug.LogError("[Spike] SpikeVideoReceiver class not found");
                return;
            }
            var jSource = AndroidJNI.NewStringUTF(_config.source);
            try
            {
                var method = AndroidJNI.GetStaticMethodID(cls, "start", "(Ljava/lang/Object;Ljava/lang/String;II)V");
                var args = new jvalue[4];
                args[0].l = _surface;
                args[1].l = jSource;
                args[2].i = _config.resW;
                args[3].i = _config.resH;
                AndroidJNI.CallStaticVoidMethod(cls, method, args);
                Debug.Log("[Spike] receiver started source=" + _config.source);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(jSource);
                AndroidJNI.DeleteLocalRef(cls);
            }
#endif
        }

        private void StopReceiver()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var cls = AndroidJNI.FindClass("tech/sukima/chattermascot/SpikeVideoReceiver");
            if (cls == IntPtr.Zero) return;
            try
            {
                var method = AndroidJNI.GetStaticMethodID(cls, "stop", "()V");
                AndroidJNI.CallStaticVoidMethod(cls, method, new jvalue[0]);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(cls);
            }
#endif
        }

        private IEnumerator LogFps()
        {
            var startFrame = Time.frameCount;
            var startTime = Time.realtimeSinceStartup;
            while (true)
            {
                yield return new WaitForSecondsRealtime(5f);
                var now = Time.realtimeSinceStartup;
                Debug.Log("[Spike] frame=" + Time.frameCount + " fps=" + ((Time.frameCount - startFrame) / (now - startTime)).ToString("F1"));
                startFrame = Time.frameCount;
                startTime = now;
            }
        }
    }
}
