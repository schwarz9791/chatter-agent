using System;
using UnityEngine;

namespace ChatterMascot.Ui
{
    /// <summary>
    /// 端末に短い通知を出す（Android の <c>android.widget.Toast</c>）。
    ///
    /// ★ <b>ここは JNI を呼ぶだけの薄いアダプタ。</b> 「何を出すか」の判断は
    ///   <c>Net.AssetSyncClient.DescribeResult</c> のような純粋関数側に置く——
    ///   <c>ChatterMascot.Tests</c> から固定できるのは判断の方だけで、JNI は固定できない。
    ///
    /// ★ <b>Android 以外では何もしない。</b> デスクトップには設定パネルとメニューバーがあり、
    ///   そちらに出す口が既にある。
    /// </summary>
    public static class DeviceToast
    {
        /// <summary>Android の <c>Toast.LENGTH_LONG</c>。長い方でも数秒しか出ない。</summary>
        private const string DurationField = "LENGTH_LONG";

        /// <summary>
        /// <paramref name="message"/> を出す。<b>失敗しても投げない</b>——通知が出ないことより、
        /// 通知のために本体が止まることの方が悪い。
        /// </summary>
        public static void Show(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            if (Application.platform != RuntimePlatform.Android) return;

            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    // ★ activity を using で包まないこと。runOnUiThread は「後で走らせる」ので、
                    //   ここを抜けた時点で破棄すると、Runnable が動く頃には参照が死んでいる。
                    //   破棄は Runnable の最後で行う
                    var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    if (activity == null) return;

                    activity.Call("runOnUiThread", new AndroidJavaRunnable(() => ShowOnUiThread(activity, message)));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] 通知を出せませんでした: " + e.Message);
            }
        }

        private static void ShowOnUiThread(AndroidJavaObject activity, string message)
        {
            try
            {
                using (var toastClass = new AndroidJavaClass("android.widget.Toast"))
                using (var toast = toastClass.CallStatic<AndroidJavaObject>(
                           "makeText", activity, message, toastClass.GetStatic<int>(DurationField)))
                {
                    toast.Call("show");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] 通知を出せませんでした: " + e.Message);
            }
            finally
            {
                activity.Dispose();
            }
        }
    }
}
