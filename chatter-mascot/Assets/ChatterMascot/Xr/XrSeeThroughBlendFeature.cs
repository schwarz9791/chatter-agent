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
    /// environment blend mode を ADDITIVE にし、ランタイムが ADDITIVE を持たなければ ALPHA_BLEND にする。
    /// 描かなかった所（背景のアルファ 0 の黒）から部屋が見えるようにするため。
    /// グラス（光学シースルー）は OPAQUE / ADDITIVE、ヘッドセット（ビデオパススルー）は OPAQUE / ALPHA_BLEND を持つ。
    ///
    /// ★ <b>どちらを要求するかは、ランタイムが持つモードを列挙して決める。</b> <c>SetEnvironmentBlendMode</c> は
    ///   次のフレームで適用する予約で、<c>GetEnvironmentBlendMode</c> は予約を反映しない。「要求して通らなければ
    ///   次」とは書けず、最後に要求したモードを持たなければ OPAQUE に戻される。
    /// ★ <b>AR Camera（パススルー）では代わりにならない。</b> あちらは ALPHA_BLEND しか要求しないので、
    ///   グラスでは OPAQUE に戻される。
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
            // 列挙できなければ ADDITIVE を要求する（グラスでの見え方を保つ）
            var target = available == null || available.Contains((int)XrEnvironmentBlendMode.Additive)
                ? XrEnvironmentBlendMode.Additive
                : available.Contains((int)XrEnvironmentBlendMode.AlphaBlend)
                    ? XrEnvironmentBlendMode.AlphaBlend
                    : mode;
            if (target == mode) return;

            SetEnvironmentBlendMode(target);
            Debug.Log($"[Mascot] XR: environment blend mode を {mode} から {target} へ要求しました");
        }

        private int[] EnumerateBlendModes()
        {
            try
            {
                var getProcAddr = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrFn>(xrGetInstanceProcAddr);
                if (getProcAddr(_instance, "xrEnumerateEnvironmentBlendModes", out var fn) != XrResult.Success
                    || fn == IntPtr.Zero)
                    return null;

                var enumerate = Marshal.GetDelegateForFunctionPointer<EnumerateEnvironmentBlendModesFn>(fn);
                if (enumerate(_instance, _systemId, PrimaryStereo, 0, out var count, null) != XrResult.Success)
                    return null;
                var modes = new int[count];
                return enumerate(_instance, _systemId, PrimaryStereo, count, out count, modes) == XrResult.Success
                    ? modes
                    : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Mascot] XR: environment blend mode を列挙できませんでした: {e.Message}");
                return null;
            }
        }
    }
}
