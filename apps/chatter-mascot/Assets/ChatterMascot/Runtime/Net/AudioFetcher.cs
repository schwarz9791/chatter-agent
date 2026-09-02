using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace ChatterMascot.Net
{
    public enum AudioFetchKind
    {
        /// <summary>200。WAV が取れた</summary>
        Ready,

        /// <summary>
        /// 503。サーバーはいるが音声を用意できない（エンジンが落ちている / 合成が返らない）。
        /// <b>あとで取りに来い</b>という意味なので、試行回数を消費せずに待つ。
        /// </summary>
        Unavailable,

        /// <summary>
        /// 404。永久に用意できない（キューから消えた / 世代違い / 読み上げる中身が無い /
        /// <c>ttsEnabled: false</c>）。諦めて ack する。
        /// </summary>
        Gone,

        /// <summary>転送そのものの失敗（接続不可・タイムアウト・想定外のステータス）。試行回数を消費する</summary>
        Failed,
    }

    public readonly struct AudioFetchResult
    {
        public readonly AudioFetchKind Kind;
        public readonly byte[] Wav;
        public readonly string Reason;

        private AudioFetchResult(AudioFetchKind kind, byte[] wav, string reason)
        {
            Kind = kind;
            Wav = wav;
            Reason = reason;
        }

        public static AudioFetchResult Ready(byte[] wav) => new AudioFetchResult(AudioFetchKind.Ready, wav, null);
        public static AudioFetchResult Unavailable(string reason) => new AudioFetchResult(AudioFetchKind.Unavailable, null, reason);
        public static AudioFetchResult Gone(string reason) => new AudioFetchResult(AudioFetchKind.Gone, null, reason);
        public static AudioFetchResult Failed(string reason) => new AudioFetchResult(AudioFetchKind.Failed, null, reason);
    }

    /// <summary>
    /// 合成済み音声を取りに行く。<c>core/src/player/audioFetcher.ts</c> の移植。
    ///
    /// ★ <b>結果を4つに分けること。</b> ここが2値（成功 / 失敗）だと、
    ///   <c>SynthesisAttempts</c>（既定2）が数 ms で燃え尽きる。合成エンジンが落ちているだけで
    ///   <b>溜まっていたキューが数百 ms で全部捨てられる</b>。
    ///
    /// ★ <b><c>UnityWebRequestMultimedia.GetAudioClip</c> を URL に直接使わないこと。</b>
    ///   契約が禁じるストリーム再生になりうるうえ、503 / 404 の<b>本文（診断の理由）</b>が取れない。
    ///   無音の原因を残す唯一の窓なのでこれは落とせない。
    /// </summary>
    public sealed class AudioFetcher
    {
        /// <summary>サーバーが本文に載せた理由。読めなければ既定の文言のまま。</summary>
        private const int ReasonMaxChars = 300;

        public readonly string BaseUrl;
        private readonly int _timeoutSeconds;

        /// <param name="baseUrl">
        /// 音声の取得元。WebSocket の接続先から導出する（<c>ws://host:port</c> → <c>http://host:port</c>）。
        ///
        /// ★ サーバーは自分の到達アドレスを知らないので、フレームに載るのは<b>相対パス</b>だけ。
        ///   authority を補うのはクライアントの責務。
        /// </param>
        /// <param name="timeoutMs">
        /// 1リクエストの上限。<b>省略できない。</b> 返らない相手を掴むと head-of-line blocking で
        /// 以後すべてが無音になり、エラーも1行も出ない。
        ///
        /// ★ <b>サーバー側の設定と突き合わせる必要は無い</b> — サーバーが応答を
        ///   <c>synthesisTimeoutMs</c> で打ち切って 503 を返すので、待たされ続ける側は塞がっている。
        /// </param>
        public AudioFetcher(string baseUrl, int timeoutMs)
        {
            BaseUrl = baseUrl;
            // UnityWebRequest.timeout は秒単位の int。0 は「無制限」なので必ず 1 以上にする
            _timeoutSeconds = Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0));
        }

        /// <summary>
        /// <c>ws://host:port</c> / <c>wss://host:port</c> を音声の取得元に読み替える。
        ///
        /// ★ 実体は <see cref="ServerUrl.ToHttpBase"/>。制御 API（#76）も同じ導出を使うので、
        ///   計算そのものは1箇所に出してある（この名前は #29 からの呼び出し側のために残す）。
        /// </summary>
        public static string DeriveAudioBaseUrl(string serverUrl)
        {
            return ServerUrl.ToHttpBase(serverUrl);
        }

        public async Task<AudioFetchResult> FetchAsync(string audioPath)
        {
            using (var request = UnityWebRequest.Get(BaseUrl + audioPath))
            {
                request.timeout = _timeoutSeconds;
                // ★ DownloadHandlerBuffer で受けること。GetAudioClip だとステータスごとの分岐と
                //   本文（診断の理由）が取れない
                request.downloadHandler = new DownloadHandlerBuffer();

                try
                {
                    await SendAsync(request);
                }
                catch (Exception e)
                {
                    return AudioFetchResult.Failed(e.Message);
                }

                // ★ **ボディを読むこと。** サーバーは 503 / 404 の本文に理由を載せてくるので、
                //   ここで捨てると無音の原因がクライアント側のログから消える
                if (request.responseCode == 503)
                {
                    return AudioFetchResult.Unavailable(Reason(request, "サーバーが音声を用意できていません (503)"));
                }
                if (request.responseCode == 404)
                {
                    return AudioFetchResult.Gone(Reason(request, "音声がありません (404)"));
                }

                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    return AudioFetchResult.Failed(request.error ?? "接続できませんでした");
                }
                if (request.result != UnityWebRequest.Result.Success || request.responseCode != 200)
                {
                    return AudioFetchResult.Failed(Reason(request, $"想定外のステータス {request.responseCode}"));
                }

                var data = request.downloadHandler.data;
                if (data == null || data.Length == 0)
                {
                    return AudioFetchResult.Failed("本体が空でした");
                }
                return AudioFetchResult.Ready(data);
            }
        }

        private static string Reason(UnityWebRequest request, string fallback)
        {
            string body = null;
            try
            {
                var data = request.downloadHandler?.data;
                if (data != null && data.Length > 0) body = Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                body = null;
            }

            if (string.IsNullOrEmpty(body)) return fallback;
            var trimmed = body.Trim();
            if (trimmed.Length == 0) return fallback;
            return trimmed.Length > ReasonMaxChars ? trimmed.Substring(0, ReasonMaxChars) : trimmed;
        }

        /// <summary>
        /// <c>UnityWebRequest</c> を <c>await</c> できる形にする。
        ///
        /// ★ <c>completed</c> は Unity のメインスレッドで呼ばれ、await の継続も
        ///   Unity の <c>SynchronizationContext</c> でメインスレッドに戻る。
        ///   だから呼び出し側は <c>AudioSource</c> や <c>AudioClip</c> をそのまま触れる。
        /// </summary>
        private static Task SendAsync(UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<bool>();
            var operation = request.SendWebRequest();
            operation.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
