using Google.XR.Extensions;
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.XR.OpenXR;

namespace ChatterMascot.EditorTools
{
    /// <summary>Spike: writes the Spatial Container settings into the Android OpenXR settings.</summary>
    public static class SpatialContainerSpike
    {
        public static void Apply()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null)
            {
                Debug.LogError("[Build] Android OpenXR Settings not found");
                EditorApplication.Exit(1);
                return;
            }

            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);

            var session = settings.GetFeature<XRSessionFeature>();
            var container = settings.GetFeature<XRSpatialContainerFeature>();
            if (session == null || container == null)
            {
                Debug.LogError($"[Build] Spatial Container spike: session={session != null} container={container != null}");
                EditorApplication.Exit(1);
                return;
            }

            session.enabled = true;
            container.enabled = true;
            session.ImmersiveXR = false;

            var serialized = new SerializedObject(session);
            SetBool(serialized, "_useSpatialApi", true);
            SetBool(serialized, "_spatialApiRequired", true);
            SetBool(serialized, "_vulkanSubsampling", false);
            var level = serialized.FindProperty("_spatialApiTargetVersion");
            level.intValue = (int)XRSpatialSdkVersions.XRSpatialApiLevelAuto;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(session);
            EditorUtility.SetDirty(container);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Build] Spatial Container spike: session.enabled={session.enabled} immersive={session.ImmersiveXR} " +
                      $"useSpatialApi={session.UseXRSpatialApi} required={session.XRSpatailApiRequired} " +
                      $"target={session.TargetApiVersion} container.enabled={container.enabled} bounds={container.SuggestedBounds}");
        }

        private static void SetBool(SerializedObject serialized, string name, bool value)
        {
            var property = serialized.FindProperty(name);
            if (property == null)
            {
                Debug.LogError($"[Build] Spatial Container spike: field {name} not found");
                return;
            }
            property.boolValue = value;
        }
    }
}
