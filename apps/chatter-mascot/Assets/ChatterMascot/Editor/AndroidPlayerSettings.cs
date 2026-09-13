using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

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
    ///   例外なので、<c>adb reverse</c> で繋いでいる間は気づけない。#98 で LAN 上のホストへ
    ///   http で繋ぐようになった瞬間に音声の取得だけが落ちるので、ここで倒しておく。
    ///   ★ これは Unity 側の判定で、Android 自体の平文通信の許可（マニフェストの
    ///   <c>usesCleartextTraffic</c>）とは別物。そちらは
    ///   <see cref="AndroidManifestPostProcessor"/> が書く。
    ///
    /// ★ <b>targetSdk は触らない。</b> <c>Automatic</c>（既定）のままにして、
    ///   Unity が対応する最新の SDK に追従させる。
    /// </summary>
    public static class AndroidPlayerSettings
    {
        /// <summary>Standalone の <c>tech.sukima.chatter-mascot</c> からハイフンを抜いたもの。</summary>
        private const string ApplicationId = "tech.sukima.chattermascot";

        private const AndroidSdkVersions MinSdkVersion = (AndroidSdkVersions)30;

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

            if (!changed)
            {
                Debug.Log("[Build] Android の Player Settings は既に出荷値でした");
                return;
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[Build] Android の Player Settings を出荷値にしました");
        }
    }
}
