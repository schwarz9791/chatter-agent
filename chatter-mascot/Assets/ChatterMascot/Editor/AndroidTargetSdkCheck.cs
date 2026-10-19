using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace ChatterMascot.EditorTools
{
    /// <summary>
    /// Android ビルドの開始時に targetSdk を検査する（#115）。
    ///
    /// ★ <b>なぜ検査するか。</b> マニフェストは <c>ACCESS_LOCAL_NETWORK</c> を宣言していない。
    ///   <c>Automatic</c> はエディタの更新だけで上がるので、LAN への接続が無言で止まる APK を出す代わりに
    ///   ビルドを止める。
    ///
    /// ★ <b>なぜビルド前か。</b> Gradle 生成後のフックで止めても成果物は書き出される。
    ///   値はビルド開始時に分かるので、何も作らないうちに止める。
    /// </summary>
    public sealed class AndroidTargetSdkCheck : IPreprocessBuildWithReport
    {
        /// <summary><c>ACCESS_LOCAL_NETWORK</c> が要るようになる API レベル（Android 側の事実）。</summary>
        private const int LocalNetworkPermissionSdk = 37;

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;

            Check((int)PlayerSettings.Android.targetSdkVersion);
        }

        /// <summary>Automatic（0）または <see cref="LocalNetworkPermissionSdk"/> 以上なら <see cref="BuildFailedException"/>。</summary>
        public static void Check(int targetSdk)
        {
            if (targetSdk > 0 && targetSdk < LocalNetworkPermissionSdk) return;

            var fixedSdk = (int)AndroidPlayerSettings.TargetSdkVersion;
            throw new BuildFailedException(
                $"[Build] targetSdk is {targetSdk} (0 means Automatic). Pin it to {fixedSdk}."
                + $" With the Editor open: set Edit > Project Settings > Player > Android > Other Settings > Target API Level to {fixedSdk}."
                + " With the Editor closed: ./scripts/run.sh ChatterMascot.EditorTools.AndroidPlayerSettings.FixAll"
                + $". Raising it to {LocalNetworkPermissionSdk} or higher requires declaring ACCESS_LOCAL_NETWORK and requesting it at runtime"
                + " (see docs/knowledge/mascot-android-xr.md, \"ネットワークまわりの根拠と未着手\")");
        }
    }
}
