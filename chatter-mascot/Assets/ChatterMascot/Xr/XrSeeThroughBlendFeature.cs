using System;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.NativeTypes;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace ChatterMascot.Xr
{
    /// <summary>
    /// ランタイムが推奨する順（<c>xrEnumerateEnvironmentBlendModes</c> の並び）で最初の OPAQUE 以外の
    /// environment blend mode を要求する。描かなかった所（背景のアルファ 0 の黒）から部屋が見えるように
    /// するため。列挙できなければ ADDITIVE を要求する。
    ///
    /// ★ <b>何を要求するかは、ランタイムが持つモードを列挙して決める。</b> <c>SetEnvironmentBlendMode</c> は
    ///   後で適用される予約で、<c>GetEnvironmentBlendMode</c> は予約を反映しない。「要求して通らなければ
    ///   次」とは書けず、最後に要求したモードを持たなければ OPAQUE に戻される。
    /// ★ <b>AR Camera（パススルー）では代わりにならない。</b> <c>ARCameraFeature</c> はパススルーの有無に
    ///   応じて ALPHA_BLEND / OPAQUE を要求し、ADDITIVE は要求しない。グラスでは OPAQUE に戻される。併用すると、
    ///   カメラ停止時の OPAQUE の予約でこちらの要求が上書きされうる。
    /// ★ 呼ばれるのはセッションの準備時（<c>XrSetupConfigValues</c>）だけ（<c>ARCameraFeature</c> と同じ形）。
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "Chatter Mascot: See-Through Blend",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        Company = "sukima.tech",
        Desc = "Selects the additive or alpha-blend environment blend mode so the room shows through unrendered pixels.",
        FeatureId = FeatureId,
        Version = "0.1.0")]
#endif
    public sealed class XrSeeThroughBlendFeature : OpenXRFeature
    {
        public const string FeatureId = "tech.sukima.chattermascot.see-through-blend";

        /// <summary><c>XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO</c>。</summary>
        private const int PrimaryStereo = 2;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate XrResult GetInstanceProcAddrFn(ulong instance,
            [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate XrResult EnumerateEnvironmentBlendModesFn(ulong instance, ulong systemId,
            int viewConfigurationType, uint capacityInput, out uint countOutput, [Out] int[] modes);

        private ulong _instance;
        private ulong _systemId;

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            _instance = xrInstance;
            return true;
        }

        protected override void OnSystemChange(ulong xrSystem) => _systemId = xrSystem;

        protected override void OnEnvironmentBlendModeChange(XrEnvironmentBlendMode mode)
        {
            var available = EnumerateBlendModes();
            var target = available == null
                ? XrEnvironmentBlendMode.Additive
                : (XrEnvironmentBlendMode)available.FirstOrDefault(m => m != (int)XrEnvironmentBlendMode.Opaque);
            var list = available == null ? "列挙できず" : string.Join(", ", available.Select(m => (XrEnvironmentBlendMode)m));

            if (target == 0)
            {
                Debug.Log($"[Mascot] XR: environment blend mode は OPAQUE 以外を持たないので {mode} のまま（持つモード: {list}）");
                return;
            }
            if (target == mode) return;

            SetEnvironmentBlendMode(target);
            Debug.Log($"[Mascot] XR: environment blend mode を {mode} から {target} へ要求しました（持つモード: {list}）");
        }

        private int[] EnumerateBlendModes()
        {
            try
            {
                var getProcAddr = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrFn>(xrGetInstanceProcAddr);
                var getProcAddrResult = getProcAddr(_instance, "xrEnumerateEnvironmentBlendModes", out var fn);
                if (getProcAddrResult != XrResult.Success || fn == IntPtr.Zero)
                {
                    Debug.LogWarning($"[Mascot] XR: environment blend mode を列挙できませんでした: xrGetInstanceProcAddr → {getProcAddrResult}");
                    return null;
                }

                var enumerate = Marshal.GetDelegateForFunctionPointer<EnumerateEnvironmentBlendModesFn>(fn);
                var countResult = enumerate(_instance, _systemId, PrimaryStereo, 0, out var count, null);
                if (countResult != XrResult.Success)
                {
                    Debug.LogWarning($"[Mascot] XR: environment blend mode を列挙できませんでした: xrEnumerateEnvironmentBlendModes → {countResult}");
                    return null;
                }

                var modes = new int[count];
                var enumerateResult = enumerate(_instance, _systemId, PrimaryStereo, count, out count, modes);
                if (enumerateResult != XrResult.Success)
                {
                    Debug.LogWarning($"[Mascot] XR: environment blend mode を列挙できませんでした: xrEnumerateEnvironmentBlendModes → {enumerateResult}");
                    return null;
                }

                Array.Resize(ref modes, (int)count);
                return modes;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mascot] XR: environment blend mode を列挙できませんでした: {e.Message}");
                return null;
            }
        }
    }
}
