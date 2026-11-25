using System.Collections;
using Google.XR.Extensions;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;

namespace ChatterMascot.Xr
{
    /// <summary>Spike: logs Spatial Container state, events and the frame loop.</summary>
    public sealed class SpatialContainerSpike : MonoBehaviour
    {
        private const string Prefix = "[Mascot] XR spike:";
        private const float HeartbeatSeconds = 5f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bind()
        {
            var host = new GameObject("SpatialContainerSpike");
            DontDestroyOnLoad(host);
            host.AddComponent<SpatialContainerSpike>();
        }

        private void OnEnable()
        {
            XRSpatialContainerFeature.OnSpatialContainerBoundsChanged += OnBoundsChanged;
            XRSpatialContainerFeature.OnSpatialContainerStateChanged += OnStateChanged;
            XRSpatialContainerFeature.OnSpatialContainerClosed += OnClosed;
            XRSpatialContainerFeature.OnSpatialContainerBoundsModeRequestDenied += OnBoundsModeDenied;
            XRSpatialContainerFeature.OnSpatialContainerVisibilityRequestDenied += OnVisibilityDenied;
        }

        private void OnDisable()
        {
            XRSpatialContainerFeature.OnSpatialContainerBoundsChanged -= OnBoundsChanged;
            XRSpatialContainerFeature.OnSpatialContainerStateChanged -= OnStateChanged;
            XRSpatialContainerFeature.OnSpatialContainerClosed -= OnClosed;
            XRSpatialContainerFeature.OnSpatialContainerBoundsModeRequestDenied -= OnBoundsModeDenied;
            XRSpatialContainerFeature.OnSpatialContainerVisibilityRequestDenied -= OnVisibilityDenied;
        }

        private IEnumerator Start()
        {
            while (XRGeneralSettings.Instance?.Manager?.activeLoader == null) yield return null;
            yield return new WaitForSeconds(1f);

            Debug.Log($"{Prefix} runtime={OpenXRRuntime.name} version={OpenXRRuntime.version}");
            Debug.Log($"{Prefix} available extensions: {string.Join(" ", OpenXRRuntime.GetAvailableExtensions())}");
            Debug.Log($"{Prefix} XR_EXT_spatial_container={OpenXRRuntime.IsExtensionEnabled("XR_EXT_spatial_container")} " +
                      $"XR_EXT_spatial_container_self_rendering={OpenXRRuntime.IsExtensionEnabled("XR_EXT_spatial_container_self_rendering")}");
            Debug.Log($"{Prefix} feature IsExtensionEnabled={XRSpatialContainerFeature.IsExtensionEnabled?.ToString() ?? "null"}");
            Debug.Log($"{Prefix} {Query()}");

            var wait = new WaitForSeconds(HeartbeatSeconds);
            while (true)
            {
                yield return wait;
                Debug.Log($"{Prefix} heartbeat frame={Time.frameCount} focused={Application.isFocused} {Query()}");
            }
        }

        private static string Query()
        {
            var state = XRSpatialContainerFeature.TryGetState(out var s);
            var bounds = XRSpatialContainerFeature.TryGetBounds(out var b);
            return $"TryGetState={state} state={s} TryGetBounds={bounds} bounds={b}";
        }

        private void OnApplicationFocus(bool focus) => Debug.Log($"{Prefix} OnApplicationFocus={focus} frame={Time.frameCount}");

        private void OnApplicationPause(bool pause) => Debug.Log($"{Prefix} OnApplicationPause={pause} frame={Time.frameCount}");

        private static void OnBoundsChanged(Vector3 bounds, bool immersive) =>
            Debug.Log($"{Prefix} bounds changed: {bounds} immersive={immersive}");

        private static void OnStateChanged(XRSpatialContainerStateFlags state, bool changed) =>
            Debug.Log($"{Prefix} state changed: {state} flag={changed}");

        private static void OnClosed() => Debug.Log($"{Prefix} container closed");

        private static void OnBoundsModeDenied() => Debug.Log($"{Prefix} bounds mode request denied");

        private static void OnVisibilityDenied() => Debug.Log($"{Prefix} visibility request denied");
    }
}
