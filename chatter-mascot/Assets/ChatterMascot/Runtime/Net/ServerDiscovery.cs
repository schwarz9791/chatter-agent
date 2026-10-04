using System;
using UnityEngine;

namespace ChatterMascot.Net
{
    /// <summary>
    /// LAN 上の <c>chatter-agent-server</c> を DNS-SD（<c>_chatter-agent._tcp</c>）で探す（Android の <c>NsdManager</c>）。
    ///
    /// ★ <b>ここは JNI を呼ぶだけの薄いアダプタ。</b> 探索そのものは Java 側
    ///   （<c>ServerDiscovery.java</c>）で完結し、こちらは結果を取りに行くだけ。
    ///   「探すかどうか」の判断は <see cref="ShouldDiscover"/> に置く（EditMode から固定できるのはそちら）。
    ///
    /// ★ <b>Android 以外では何もしない。</b> サーバーは広告を非ループバックで待ち受けているときだけ出す。
    /// ★ 失敗しても投げない（<see cref="Debug.LogWarning(object)"/> に落とす）。
    /// </summary>
    public static class ServerDiscovery
    {
        private const string JavaClass = "tech.sukima.chattermascot.ServerDiscovery";

        /// <summary>
        /// 設定ファイルに接続先が無く、トークンがあるときだけ探す。
        ///
        /// ★ <b>許可リストで書くこと。</b> 「Android 以外を除く」と否定で書くと、新しいプラットフォームが
        ///   黙って探索の対象になる。
        /// ★ トークンが要る理由: 非ループバックの接続にはトークンが要る。無いときは従来どおり既定
        ///   （<c>adb reverse</c> 経由のループバック）を使う。
        /// </summary>
        public static bool ShouldDiscover(RuntimePlatform platform, string fileServerUrl, string token)
        {
            switch (platform)
            {
                case RuntimePlatform.Android:
                    return string.IsNullOrEmpty(fileServerUrl) && !string.IsNullOrEmpty(token);
                default:
                    return false;
            }
        }

        /// <summary>探索を始められたら <c>true</c>。</summary>
        public static bool Start()
        {
            if (Application.platform != RuntimePlatform.Android) return false;

            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var java = new AndroidJavaClass(JavaClass))
                {
                    if (activity == null) return false;
                    java.CallStatic("start", activity);
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] サーバーの探索を始められませんでした: " + e.Message);
                return false;
            }
        }

        /// <summary>解決できていれば <c>true</c>。</summary>
        public static bool TryTake(out string host, out int port, out string name)
        {
            host = null;
            port = 0;
            name = null;
            if (Application.platform != RuntimePlatform.Android) return false;

            try
            {
                using (var java = new AndroidJavaClass(JavaClass))
                {
                    var result = java.CallStatic<string[]>("poll");
                    if (result == null || result.Length < 3) return false;
                    if (!int.TryParse(result[1], out port)) return false;
                    host = result[0];
                    name = result[2];
                    return true;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] サーバーの探索結果を読めませんでした: " + e.Message);
                return false;
            }
        }

        public static void Stop()
        {
            if (Application.platform != RuntimePlatform.Android) return;

            try
            {
                using (var java = new AndroidJavaClass(JavaClass))
                {
                    java.CallStatic("stop");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] サーバーの探索を止められませんでした: " + e.Message);
            }
        }
    }
}
