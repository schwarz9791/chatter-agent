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
    /// environment blend mode を ADDITIVE にする。描かなかった所（背景のアルファ 0 の黒）から
    /// 部屋が見えるようにするため。
    ///
    /// ★ <b>AR Camera（パススルー）では代わりにならない。</b> あちらは ALPHA_BLEND を要求するが、
    ///   グラスのランタイムは OPAQUE / ADDITIVE しか持たず、既定で OPAQUE が選ばれる。
    ///   ADDITIVE を持たないランタイム（ヘッドセット）では、要求しても既定のまま。
    /// ★ ランタイムが途中で戻しても、ここが呼ばれるので付け直す（<c>ARCameraFeature</c> と同じ形）。
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(UiName = "Chatter Mascot: Additive Blend",
        BuildTargetGroups = new[] { BuildTargetGroup.Android },
        Company = "sukima.tech",
        Desc = "Selects the additive environment blend mode so the room shows through unrendered pixels.",
        FeatureId = FeatureId,
        Version = "0.1.0")]
#endif
    public sealed class XrAdditiveBlendFeature : OpenXRFeature
    {
        public const string FeatureId = "tech.sukima.chattermascot.additive-blend";

        protected override void OnEnvironmentBlendModeChange(XrEnvironmentBlendMode mode)
        {
            if (mode == XrEnvironmentBlendMode.Additive) return;

            SetEnvironmentBlendMode(XrEnvironmentBlendMode.Additive);
            Debug.Log($"[Mascot] XR: environment blend mode を {mode} から Additive へ要求しました");
        }
    }
}
