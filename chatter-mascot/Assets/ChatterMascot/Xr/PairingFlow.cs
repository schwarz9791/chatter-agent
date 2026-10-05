using System;
using System.Threading.Tasks;
using ChatterMascot.Net;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using ChatterMascot.Vrm;
using UnityEngine;

namespace ChatterMascot.Xr
{
    /// <summary>
    /// ペアリングの本体。XR の設定パネル（<see cref="XrSettingsBridge"/>）と、設定パネルの無い
    /// 端末のダイアログ（<see cref="PromptAsync"/>）が共有する。UI は呼び手が持つ。
    /// </summary>
    public static class PairingFlow
    {
        /// <summary>サーバーを LAN から探す待ち時間（秒）。</summary>
        private const float PairingDiscoverySeconds = 10f;

        /// <summary>ペアリングの要求 1 回の上限（ミリ秒）。</summary>
        private const int PairingTimeoutMs = 10000;

        /// <summary>
        /// PIN をサーバーへ送り、受け取ったトークンを保存してその場で繋ぎ直す。
        ///
        /// ★ <b>接続先（<c>serverUrl</c>）は保存しない。</b> 設定に無ければ次回の起動も探索で見つける。
        /// ★ await のたびに <paramref name="runner"/> / <paramref name="host"/> の破棄を見る。
        ///   破棄された後に進めない。
        /// </summary>
        public static async Task<PairingResult> RunAsync(MascotSettingsHost host, MascotRunner runner, string pin)
        {
            var url = host.Current.ServerUrl;
            if (string.IsNullOrEmpty(url))
            {
                url = await runner.FindServerForPairingAsync(PairingDiscoverySeconds);
                if (runner == null || host == null) return PairingResult.Of(PairingKind.Unreachable);
            }
            if (string.IsNullOrEmpty(url)) return PairingResult.Of(PairingKind.NotFound);

            var result = await PairingClient.ClaimAsync(ServerUrl.ToHttpBase(url), pin, PairingTimeoutMs);
            if (runner == null || host == null) return PairingResult.Of(PairingKind.Unreachable);
            if (result.Kind != PairingKind.Paired) return result;

            // ★ トークンだけ保存する。値そのものはログに出さない
            host.Apply(host.Current.WithToken(result.Token));
            if (!runner.Reconnect(url, result.Token)) return PairingResult.Of(PairingKind.BadResponse);
            if (host.Current.AssetSync != SettingsMapping.AssetSyncOff) runner.TryStartAssetSync(requested: false);

            return result;
        }

        /// <summary>
        /// 設定パネルの無い端末で、ダイアログから PIN を受けてペアリングする。
        /// 取り消されるまで、失敗の理由を添えて出し直す。
        /// </summary>
        public static async Task PromptAsync(MascotRunner runner)
        {
            try
            {
                var text = UiText.For(Application.systemLanguage);
                var message = text.PairingDialogMessage;

                while (true)
                {
                    var typed = await PinDialog.AskAsync(
                        text.XrPairUnreachable, message, text.XrPairingPin, text.XrPair, text.Cancel);
                    if (typed == null) return;

                    var pin = SettingsSchema.ParseTypedPin(typed);
                    if (pin == null)
                    {
                        message = text.XrPairKeyboardInvalid + "\n\n" + text.PairingDialogMessage;
                        continue;
                    }

                    var host = MascotSettingsHost.Instance;
                    if (runner == null || host == null) return;

                    DeviceToast.Show(text.XrPairing);
                    var result = await RunAsync(host, runner, pin);
                    if (result.Kind == PairingKind.Paired)
                    {
                        Debug.Log("[Mascot] ペアリングして繋ぎ直しました");
                        DeviceToast.Show(PairingClient.Describe(result, text));
                        return;
                    }
                    if (runner == null || host == null) return;

                    message = PairingClient.Describe(result, text) + "\n\n" + text.PairingDialogMessage;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Mascot] ペアリングのダイアログで例外が出ました: " + e);
            }
        }
    }
}
