using System;
using System.Threading.Tasks;
using UnityEngine;

namespace ChatterMascot.Ui
{
    /// <summary>
    /// 4 桁の PIN を受けるネイティブのダイアログ（<c>PinDialog.java</c>）。
    ///
    /// ★ <b>ここは JNI を呼ぶだけの薄いアダプタ。</b> 入力の検証はしない——呼び出し側が
    ///   <c>SettingsSchema.ParseTypedPin</c> で見る。
    /// ★ <b>Android 以外では何も出さない</b>（<c>null</c> を返す）。
    /// </summary>
    public static class PinDialog
    {
        private const string JavaClass = "tech.sukima.chattermascot.PinDialog";

        /// <summary>結果を見に行く間隔（ミリ秒）。</summary>
        private const int PollIntervalMs = 200;

        /// <summary>
        /// 入力された文字列を返す。取り消し・出せなかったときは <c>null</c>。
        /// <paramref name="closeWhen"/> が真になったらダイアログを閉じて <c>null</c> を返す
        /// （入力が要らなくなったとき）。
        /// <b>失敗しても投げない</b>——ダイアログが出ないことより、本体が止まることの方が悪い。
        /// </summary>
        public static async Task<string> AskAsync(string title, string message, string hint, string ok, string cancel,
            Func<bool> closeWhen = null)
        {
            if (Application.platform != RuntimePlatform.Android) return null;

            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var dialog = new AndroidJavaClass(JavaClass))
                {
                    if (activity == null) return null;
                    dialog.CallStatic("show", activity, title, message, hint, ok, cancel,
                        ChatterMascot.Settings.SettingsSchema.PinLength);

                    while (true)
                    {
                        if (closeWhen?.Invoke() == true)
                        {
                            dialog.CallStatic("dismiss");
                            return null;
                        }
                        if (dialog.CallStatic<bool>("isDone")) break;
                        await Task.Delay(PollIntervalMs);
                    }
                    return dialog.CallStatic<string>("take");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] PIN の入力欄を出せませんでした: " + e.Message);
                return null;
            }
        }
    }
}
