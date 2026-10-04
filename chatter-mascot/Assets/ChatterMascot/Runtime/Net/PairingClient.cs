using System;
using System.Text;
using System.Threading.Tasks;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace ChatterMascot.Net
{
    public enum PairingKind
    {
        /// <summary>トークンを受け取れた</summary>
        Paired,

        /// <summary>PIN が違う。まだ入れ直せる</summary>
        WrongPin,

        /// <summary>PIN が失効した・ロックされた・出ていない。サーバー側で出し直さない限り通らない</summary>
        Reissue,

        /// <summary>ペアリングの口が無いサーバー</summary>
        OldServer,

        /// <summary>接続できない・タイムアウト</summary>
        Unreachable,

        /// <summary>応答を読めない・トークンが使えない形</summary>
        BadResponse,
    }

    public readonly struct PairingResult
    {
        private PairingResult(PairingKind kind, string token, int remaining)
        {
            Kind = kind;
            Token = token;
            Remaining = remaining;
        }

        public PairingKind Kind { get; }

        /// <summary><see cref="PairingKind.Paired"/> のときだけ。検証済み</summary>
        public string Token { get; }

        /// <summary><see cref="PairingKind.WrongPin"/> のときの残り回数</summary>
        public int Remaining { get; }

        public static PairingResult Of(PairingKind kind) => new PairingResult(kind, null, 0);
        public static PairingResult Paired(string token) => new PairingResult(PairingKind.Paired, token, 0);
        public static PairingResult WrongPin(int remaining) => new PairingResult(PairingKind.WrongPin, null, remaining);
    }

    /// <summary>
    /// server の <c>POST /v1/pairing/claim</c> を叩いて、PIN と引き換えにトークンを受け取る。
    ///
    /// ★★ <b><c>Authorization</c> も <c>Origin</c> も付けないこと。</b> トークンをまだ持たない端末が使う口で、
    ///   <c>Origin</c> が付いた要求はサーバーが弾く。<c>UnityWebRequest</c> は既定で送らないので足さなければよい。
    /// ★ <b>受け取ったトークンは <see cref="SettingsJson.IsValidToken"/> を通してから使う。</b>
    ///   そのままヘッダに載るため、ファイルから読む値と同じ厳しさにする。値そのものをログに出さない。
    /// </summary>
    public static class PairingClient
    {
        public static async Task<PairingResult> ClaimAsync(string httpBase, string pin, int timeoutMs)
        {
            var body = new JObject { ["pin"] = pin }.ToString(Newtonsoft.Json.Formatting.None);
            using (var request = new UnityWebRequest(httpBase + "/v1/pairing/claim", "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                // UnityWebRequest.timeout は秒単位の int。0 は「無制限」なので必ず 1 以上にする
                request.timeout = Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0));

                try
                {
                    await AudioFetcher.SendAsync(request);
                }
                catch (Exception)
                {
                    return PairingResult.Of(PairingKind.Unreachable);
                }

                var connectionError = request.result == UnityWebRequest.Result.ConnectionError;
                var text = request.downloadHandler != null ? request.downloadHandler.text : null;
                return Classify(request.responseCode, connectionError, text);
            }
        }

        /// <summary>
        /// 応答を結果に分ける<b>純粋関数</b>。
        ///
        /// ★ <b>401 / 404 は本文を見ずに「古い server」</b>。ペアリングを持たない server は、
        ///   ルートに当たらない要求に本文の形が違う応答を返す。
        /// ★ 残り 0 回の <c>wrong_pin</c> は、もう入れ直せないので <see cref="PairingKind.Reissue"/> に倒す。
        /// </summary>
        public static PairingResult Classify(long status, bool connectionError, string body)
        {
            if (connectionError || status == 0) return PairingResult.Of(PairingKind.Unreachable);
            if (status == 401 || status == 404) return PairingResult.Of(PairingKind.OldServer);

            JObject root;
            try
            {
                root = JObject.Parse(body ?? "");
            }
            catch (Exception)
            {
                return PairingResult.Of(PairingKind.BadResponse);
            }

            if (status == 200)
            {
                var token = root["token"];
                if (token == null || token.Type != JTokenType.String) return PairingResult.Of(PairingKind.BadResponse);
                var value = token.Value<string>();
                return SettingsJson.IsValidToken(value)
                    ? PairingResult.Paired(value)
                    : PairingResult.Of(PairingKind.BadResponse);
            }

            if (status == 403)
            {
                var error = root["error"];
                var code = error != null && error.Type == JTokenType.String ? error.Value<string>() : null;
                switch (code)
                {
                    case "wrong_pin":
                        var remaining = root["remaining"];
                        var left = 0;
                        // ★ Value<int>() は int を超える整数で投げるので、文字列から読む
                        if (remaining != null && remaining.Type == JTokenType.Integer) int.TryParse(remaining.ToString(), out left);
                        return left > 0 ? PairingResult.WrongPin(left) : PairingResult.Of(PairingKind.Reissue);
                    case "expired":
                    case "locked":
                    case "no_pairing":
                        return PairingResult.Of(PairingKind.Reissue);
                }
            }

            return PairingResult.Of(PairingKind.BadResponse);
        }

        /// <summary>端末に出す文言。<see cref="PairingKind.Paired"/> も含む</summary>
        public static string Describe(PairingResult result, UiText text)
        {
            switch (result.Kind)
            {
                case PairingKind.Paired: return text.XrPairDone;
                case PairingKind.WrongPin: return text.XrPairWrongPin(result.Remaining);
                case PairingKind.Reissue: return text.XrPairReissue;
                case PairingKind.OldServer: return text.XrPairOldServer;
                case PairingKind.Unreachable: return text.XrPairUnreachable;
                default: return text.XrPairBadResponse;
            }
        }
    }
}
