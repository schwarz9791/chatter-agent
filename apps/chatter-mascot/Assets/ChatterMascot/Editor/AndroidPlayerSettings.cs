using System.Collections.Generic;
using System.Reflection;
using Unity.XR.CoreUtils.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Android;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// Android の Player Settings を出荷値にする（#97）。
    ///
    ///   ./scripts/run.sh ChatterMascot.EditorTools.AndroidPlayerSettings.FixAll
    ///
    /// ★ <b><c>NativePluginSettings</c> / <c>SceneFixups</c> と同じ立ち位置。</b>
    ///   Inspector の Player Settings を手で触ると「誰かのマシンでだけ通る」状態になるので、
    ///   直し方をコードに置いておく。
    ///
    /// ★ <b>パッケージ名にハイフンを使えない。</b> Standalone は
    ///   <c>tech.sukima.chatter-mascot</c> だが、Android のアプリケーション ID は
    ///   Java のパッケージ名の規則に従うため <c>-</c> を許さない。
    ///   ここだけ <c>tech.sukima.chattermascot</c> にしてある。
    ///
    /// ★ <b><c>insecureHttpOption</c> を <c>AlwaysAllowed</c> にする理由。</b>
    ///   <c>UnityWebRequest</c> は既定で http を拒むが、ループバック（<c>127.0.0.1</c>）だけは
    ///   例外なので、<c>adb reverse</c> で繋いでいる間は気づけない。LAN 上のホストへ
    ///   http で繋ぐと音声の取得だけが落ちるので、ここで倒しておく。
    ///   ★ これは Unity 側の判定で、Android 自体の平文通信の許可（マニフェストの
    ///   <c>usesCleartextTraffic</c>）とは別物。そちらは
    ///   <see cref="AndroidManifestPostProcessor"/> が書く。
    ///   ★ <b>プラットフォーム別ではなくプロジェクト全体の設定なので、macOS のスタンドアロンにも
    ///   効く。</b>
    ///
    /// ★ <b>targetSdk は触らない。</b> <c>Automatic</c>（既定）のままにして、
    ///   Unity が対応する最新の SDK に追従させる。
    /// </summary>
    public static class AndroidPlayerSettings
    {
        /// <summary>Standalone の <c>tech.sukima.chatter-mascot</c> からハイフンを抜いたもの。</summary>
        private const string ApplicationId = "tech.sukima.chattermascot";

        private const AndroidSdkVersions MinSdkVersion = (AndroidSdkVersions)30;

        /// <summary><c>Packages/manifest.json</c> の <c>com.unity.xr.androidxr-openxr</c>（#99）。</summary>
        private const string OpenXrLoaderTypeName = "UnityEngine.XR.OpenXR.OpenXRLoader";

        /// <summary>Android の既定品質レベル（"Mobile"）が使う URP Renderer。</summary>
        private const string MobileRendererPath = "Assets/Settings/Mobile_Renderer.asset";

        public static void FixAll()
        {
            var changed = false;

            if (PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) != ApplicationId)
            {
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);
                changed = true;
            }

            if (PlayerSettings.Android.minSdkVersion != MinSdkVersion)
            {
                PlayerSettings.Android.minSdkVersion = MinSdkVersion;
                changed = true;
            }

            if (!PlayerSettings.Android.forceInternetPermission)
            {
                PlayerSettings.Android.forceInternetPermission = true;
                changed = true;
            }

            if (PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed)
            {
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                changed = true;
            }

            if (FixGraphicsApi()) changed = true;
            if (FixXrLoader()) changed = true;
            if (FixOpenXrFeature()) changed = true;
            if (FixPostProcessing()) changed = true;
            CheckApplicationEntryPoint();

            if (changed)
            {
                AssetDatabase.SaveAssets();
                Debug.Log("[Build] Android の Player Settings を出荷値にしました");
            }
            else
            {
                Debug.Log("[Build] Android の Player Settings は既に出荷値でした");
            }

            LogProjectValidationIssues();
        }

        /// <summary>Android XR（Vulkan 必須）向けに Graphics API を Vulkan 単独にする。</summary>
        private static bool FixGraphicsApi()
        {
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            if (!PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android)
                && apis.Length == 1 && apis[0] == GraphicsDeviceType.Vulkan)
            {
                return false;
            }

            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            Debug.Log("[Build] Android の Graphics API を Vulkan 単独にしました");
            return true;
        }

        /// <summary>
        /// エントリポイントが GameActivity であることを確認するだけ（Android XR の必須設定）。
        /// シーンに焼かれたコンポーネントと同じ理由で、ここでは変えない（勝手に直さない）。
        /// </summary>
        private static void CheckApplicationEntryPoint()
        {
            if (PlayerSettings.Android.applicationEntry != AndroidApplicationEntry.GameActivity)
            {
                Debug.LogWarning("[Build] Android のエントリポイントが GameActivity ではありません: "
                    + PlayerSettings.Android.applicationEntry);
            }
        }

        /// <summary>
        /// XR Plug-in Management で Android にだけ OpenXR ローダーを割り当てる。
        /// Standalone には割り当てない（macOS は XR を使わない）。
        /// </summary>
        private static bool FixXrLoader()
        {
            if (XRPackageMetadataStore.IsLoaderAssigned(OpenXrLoaderTypeName, BuildTargetGroup.Android))
            {
                return false;
            }

            var generalSettings = GetOrCreateXrGeneralSettings();
            if (!generalSettings.HasSettingsForBuildTarget(BuildTargetGroup.Android))
            {
                generalSettings.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            }
            if (!generalSettings.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            {
                generalSettings.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            }

            var manager = generalSettings.ManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            if (!XRPackageMetadataStore.AssignLoader(manager, OpenXrLoaderTypeName, BuildTargetGroup.Android))
            {
                Debug.LogError("[Build] Android に OpenXR ローダーを割り当てられませんでした");
                return false;
            }

            Debug.Log("[Build] Android の XR Plug-in Management に OpenXR ローダーを割り当てました");
            return true;
        }

        /// <summary>
        /// <c>XRGeneralSettingsPerBuildTarget.GetOrCreate()</c> 相当。本体は internal で
        /// パッケージ外から呼べないため、同じ手順（<c>EditorBuildSettings</c> の config object →
        /// 既存アセット探索 → 無ければ新規作成）を自前で踏む。
        /// </summary>
        private static XRGeneralSettingsPerBuildTarget GetOrCreateXrGeneralSettings()
        {
            EditorBuildSettings.TryGetConfigObject<XRGeneralSettingsPerBuildTarget>(
                XRGeneralSettings.k_SettingsKey, out var settings);
            if (settings != null) return settings;

            var guids = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
            if (guids.Length > 0)
            {
                settings = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(
                    AssetDatabase.GUIDToAssetPath(guids[0]));
            }

            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(settings, "Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, settings, true);
            return settings;
        }

        /// <summary>
        /// OpenXR の Android XR Support feature（Android XR を動かす必須 feature）だけを有効化する。
        /// Display Utilities など任意の feature には触れない。
        /// </summary>
        private static bool FixOpenXrFeature()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null)
            {
                Debug.LogWarning("[Build] Android の OpenXR Settings が見つかりません");
                return false;
            }

            var feature = settings.GetFeature<AndroidXRSupportFeature>();
            if (feature == null)
            {
                Debug.LogWarning("[Build] Android XR Support feature が見つかりません");
                return false;
            }

            if (feature.enabled) return false;

            feature.enabled = true;
            Debug.Log("[Build] OpenXR の Android XR Support feature を有効化しました");
            return true;
        }

        /// <summary>
        /// Android XR は Post Processing 込みの URP Renderer をサポートしない（Project Validation の必須項目）。
        /// ★ <c>UniversalRendererData</c> を型で参照しない（<see cref="SceneFixups"/> と同じ流儀）。
        ///   <c>SerializedObject</c> でフィールド名を直接引く。
        /// </summary>
        private static bool FixPostProcessing()
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(MobileRendererPath);
            if (asset == null)
            {
                Debug.LogWarning($"[Build] {MobileRendererPath} が見つかりません");
                return false;
            }

            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty("postProcessData");
            if (property == null)
            {
                Debug.LogWarning($"[Build] {MobileRendererPath} に postProcessData がありません");
                return false;
            }
            if (property.objectReferenceValue == null) return false;

            property.objectReferenceValue = null;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"[Build] {MobileRendererPath} の Post Processing を無効化しました（Android XR の必須設定）");
            return true;
        }

        /// <summary>
        /// 残っている OpenXR の Project Validation の問題（Android 向け）をログに出す。
        /// <c>BuildValidator.GetCurrentValidationIssues</c> は internal（Project Validation
        /// ウィンドウ自身が同じ方法で読んでいる。公開 API が無い）なので reflection で呼ぶ。
        /// </summary>
        private static void LogProjectValidationIssues()
        {
            var method = typeof(BuildValidator).GetMethod("GetCurrentValidationIssues",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                Debug.LogWarning("[Build] Project Validation を読めませんでした（API が変わった可能性）");
                return;
            }

            var issues = new HashSet<BuildValidationRule>();
            method.Invoke(null, new object[] { issues, BuildTargetGroup.Android });

            if (issues.Count == 0)
            {
                Debug.Log("[Build] Android の Project Validation に残っている問題はありません");
                return;
            }

            foreach (var issue in issues)
            {
                Debug.Log($"[Build] Project Validation ({(issue.Error ? "error" : "warning")}): {issue.Message}");
            }
        }
    }
}
