using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace ChatterMascot.Net
{
    /// <summary>制御 API の呼び出し結果。★ 失敗を「例外」にしないのは <c>AudioFetcher</c> と同じ判断</summary>
    public readonly struct CoreResult
    {
        private CoreResult(bool ok, JToken body, byte[] bytes, string reason, long status)
        {
            Ok = ok;
            Body = body;
            Bytes = bytes;
            Reason = reason;
            Status = status;
        }

        public bool Ok { get; }

        /// <summary>成功時の JSON。バイナリを返す口では null</summary>
        public JToken Body { get; }

        /// <summary>成功時のバイナリ（<c>POST /v1/tts/preview</c>）</summary>
        public byte[] Bytes { get; }

        /// <summary>失敗の理由。<b>そのまま note に出せる日本語</b>にしてある</summary>
        public string Reason { get; }

        /// <summary>HTTP のステータス。転送エラーなら 0</summary>
        public long Status { get; }

        public static CoreResult Success(JToken body) => new CoreResult(true, body, null, null, 200);
        public static CoreResult Binary(byte[] bytes) => new CoreResult(true, null, bytes, null, 200);
        public static CoreResult Failure(string reason, long status) => new CoreResult(false, null, null, reason, status);
    }

    /// <summary>
    /// core の制御 API（<c>/v1/*</c>）を叩く。
    ///
    /// ★ <b><c>HttpClient</c> を使わないこと。</b> このプロジェクトの HTTP は
    ///   <c>UnityWebRequest</c> の1本に揃えてある（<c>AudioFetcher</c> と同じ形）。
    ///   継続がメインスレッドに戻るのも、タイムアウトが秒単位なのも、そちらに合わせる。
    ///
    /// ★★ <b>書き込み（<c>PATCH</c> / <c>POST</c>）はサーバー側でループバック限定になっている。</b>
    ///   このクライアントが同じマシンで動く前提（→ <c>docs/protocol.md</c> の「制御 API」）。
    ///   XR（#25）から設定を書く道はまだ無い。
    ///
    /// ★ <b><c>Origin</c> を付けないこと。</b> 付いた書き込みはサーバーが 403 で弾く
    ///   （WebView からの CSRF を塞ぐ仕掛け）。<c>UnityWebRequest</c> は既定で送らないので、
    ///   <b>足さなければよい</b>。
    /// </summary>
    public sealed class CoreConfigClient
    {
        /// <summary>サーバーが本文に載せた理由の上限（<c>AudioFetcher</c> と同じ）</summary>
        private const int ReasonMaxChars = 300;

        public readonly string BaseUrl;
        private readonly int _timeoutSeconds;

        /// <param name="baseUrl"><c>http://host:port</c>（→ <see cref="ServerUrl.ToHttpBase"/>）</param>
        /// <param name="timeoutMs">
        /// 1リクエストの上限。★ <b>テスト要約だけは長い</b> —— サーバー側は
        /// <c>aiSummaryTimeoutMs</c>（既定60秒）まで粘るので、
        /// <see cref="SummaryPreviewAsync"/> には別の予算を渡すこと。
        /// </param>
        public CoreConfigClient(string baseUrl, int timeoutMs)
        {
            BaseUrl = baseUrl;
            // UnityWebRequest.timeout は秒単位の int。0 は「無制限」なので必ず 1 以上にする
            _timeoutSeconds = ToSeconds(timeoutMs);
        }

        private static int ToSeconds(int timeoutMs)
        {
            return Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0));
        }

        public Task<CoreResult> HealthAsync()
        {
            return GetJsonAsync("/v1/health", _timeoutSeconds);
        }

        public Task<CoreResult> SpeakersAsync()
        {
            return GetJsonAsync("/v1/speakers", _timeoutSeconds);
        }

        public Task<CoreResult> ConfigAsync()
        {
            return GetJsonAsync("/v1/config", _timeoutSeconds);
        }

        /// <summary>
        /// 1キーだけ書き換える。
        ///
        /// ★ <b>まとめて送らないこと。</b> サーバー側は all-or-nothing なので、
        ///   1つでも弾かれると全部書かれない。UI は項目ごとに操作されるので、
        ///   1キーずつ送れば「どの項目が拒否されたか」がそのまま note に出せる。
        /// </summary>
        public Task<CoreResult> PatchConfigAsync(string key, JToken value)
        {
            var body = new JObject { [key] = value };
            return SendJsonAsync("PATCH", "/v1/config", body.ToString(Formatting.None), _timeoutSeconds);
        }

        /// <summary>テスト音声。固定文なので引数は無い（→ <c>docs/protocol.md</c>）</summary>
        public async Task<CoreResult> TtsPreviewAsync()
        {
            using (var request = MakeJsonRequest("POST", "/v1/tts/preview", "{}"))
            {
                request.timeout = _timeoutSeconds;
                await SendAsync(request);
                if (!Succeeded(request)) return Failed(request);
                return CoreResult.Binary(request.downloadHandler.data);
            }
        }

        /// <summary>
        /// テスト要約。
        ///
        /// ★ <b>専用のタイムアウトを渡すこと。</b> サーバー側は
        ///   <c>aiSummaryTimeoutMs</c>（既定60秒）まで粘る。他の口と同じ予算にすると、
        ///   サーバーは正常に答えているのにクライアント側だけ諦める形になる。
        /// ★ <b>失敗も 200 で返ってくる</b>（<c>outcome</c> に理由が入る）。
        /// </summary>
        public Task<CoreResult> SummaryPreviewAsync(int timeoutMs)
        {
            return SendJsonAsync("POST", "/v1/summary/preview", "{}", ToSeconds(timeoutMs));
        }

        private async Task<CoreResult> GetJsonAsync(string path, int timeoutSeconds)
        {
            using (var request = UnityWebRequest.Get(BaseUrl + path))
            {
                request.timeout = timeoutSeconds;
                request.downloadHandler = new DownloadHandlerBuffer();
                await SendAsync(request);
                return ReadJson(request);
            }
        }

        private async Task<CoreResult> SendJsonAsync(string method, string path, string body, int timeoutSeconds)
        {
            using (var request = MakeJsonRequest(method, path, body))
            {
                request.timeout = timeoutSeconds;
                await SendAsync(request);
                return ReadJson(request);
            }
        }

        /// <summary>
        /// ★★ <b><c>Content-Type: application/json</c> を必ず付けること。</b>
        ///   付いていない書き込みはサーバーが 415 で弾く（CSRF 対策の連鎖の一部で、
        ///   これが無いとブラウザからの simple request を塞げない）。
        /// </summary>
        private UnityWebRequest MakeJsonRequest(string method, string path, string body)
        {
            var request = new UnityWebRequest(BaseUrl + path, method);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body ?? "{}"));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            return request;
        }

        private static bool Succeeded(UnityWebRequest request)
        {
            return request.result == UnityWebRequest.Result.Success && request.responseCode == 200;
        }

        private CoreResult ReadJson(UnityWebRequest request)
        {
            if (!Succeeded(request)) return Failed(request);

            var text = request.downloadHandler != null ? request.downloadHandler.text : null;
            if (string.IsNullOrEmpty(text)) return CoreResult.Success(null);

            try
            {
                using (var reader = new JsonTextReader(new System.IO.StringReader(text)))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    return CoreResult.Success(JToken.Load(reader));
                }
            }
            catch (Exception e)
            {
                // ★ throw しない。設定パネルは「繋がらない」と同じ扱いにできればよい
                return CoreResult.Failure("応答を読めませんでした: " + e.Message, request.responseCode);
            }
        }

        /// <summary>
        /// 失敗の理由を作る。
        ///
        /// ★ <b>本文を捨てないこと。</b> 制御 API は <c>{"error":"readonly_key","key":"…"}</c> の
        ///   形で理由を返す。ここを落とすと、パネルには「失敗しました」しか出せなくなる。
        /// </summary>
        private CoreResult Failed(UnityWebRequest request)
        {
            var status = request.responseCode;
            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                return CoreResult.Failure("サーバーに繋がりません", 0);
            }

            var body = request.downloadHandler != null ? request.downloadHandler.text : null;
            var described = DescribeError(body);
            if (!string.IsNullOrEmpty(described)) return CoreResult.Failure(described, status);

            return CoreResult.Failure("エラーが返りました（HTTP " + status + "）", status);
        }

        /// <summary>
        /// 制御 API のエラー本文を日本語にする。
        ///
        /// ★ <b>知らない <c>error</c> をそのまま出すこと。</b> 訳せないものを
        ///   「不明なエラー」に潰すと、サーバー側のログと突き合わせられなくなる。
        /// </summary>
        public static string DescribeError(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;

            JObject root;
            try
            {
                root = JToken.Parse(body) as JObject;
            }
            catch (Exception)
            {
                var trimmed = body.Trim();
                if (trimmed.Length == 0) return null;
                return trimmed.Length > ReasonMaxChars ? trimmed.Substring(0, ReasonMaxChars) : trimmed;
            }

            if (root == null) return null;
            var error = root["error"];
            if (error == null || error.Type != JTokenType.String) return null;

            var key = root["key"] != null ? root["key"].ToString() : "";
            switch (error.Value<string>())
            {
                case "env_override":
                    return "環境変数で固定されているので変えられません（" + key + "）";
                case "readonly_key":
                    return "この設定は変更できません（" + key + "）";
                case "invalid_value":
                    return "値が範囲外です（" + key + "）";
                case "unknown_key":
                    return "知らない設定です（" + key + "）";
                case "engine_unreachable":
                    return "音声合成エンジンに繋がりません";
                case "config_unreadable":
                    return "config.json を読めないので書き込みませんでした";
                case "config_unwritable":
                    return "config.json に書けませんでした";
                case "too_many_requests":
                    return "続けて押しすぎです。少し待ってください";
                default:
                    return error.Value<string>() + (string.IsNullOrEmpty(key) ? "" : "（" + key + "）");
            }
        }

        /// <summary>
        /// <c>UnityWebRequest</c> を <c>await</c> できる形にする（<c>AudioFetcher</c> と同じ）。
        ///
        /// ★ <c>completed</c> は Unity のメインスレッドで呼ばれ、await の継続も
        ///   Unity の <c>SynchronizationContext</c> でメインスレッドに戻る。
        /// </summary>
        private static Task SendAsync(UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<bool>();
            var operation = request.SendWebRequest();
            operation.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }

        /// <summary>
        /// <c>GET /v1/speakers</c> の応答を選択肢に読み替える。<b>純粋関数</b>。
        ///
        /// ★ 読めない要素は落として、読めたぶんだけ返す。★ <b>1件でも壊れていたら
        ///   全部捨てる、にしないこと</b> —— 話者一覧は許可リストではないので、
        ///   出せるものを出す方が親切。
        /// </summary>
        public static List<Settings.SettingChoice> ReadSpeakers(JToken body)
        {
            var result = new List<Settings.SettingChoice>();
            var root = body as JObject;
            if (root == null) return result;

            var speakers = root["speakers"] as JArray;
            if (speakers == null) return result;

            foreach (var item in speakers)
            {
                var entry = item as JObject;
                if (entry == null) continue;
                var id = entry["id"];
                var label = entry["label"];
                if (id == null || id.Type != JTokenType.Integer) continue;
                var text = label != null && label.Type == JTokenType.String ? label.Value<string>() : id.ToString();
                result.Add(new Settings.SettingChoice(id.ToString(), text));
            }
            return result;
        }
    }
}
