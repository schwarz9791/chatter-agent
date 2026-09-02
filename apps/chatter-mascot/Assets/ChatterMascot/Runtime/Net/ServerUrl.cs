using System;

namespace ChatterMascot.Net
{
    /// <summary>
    /// WebSocket の接続先から HTTP の口を導く。
    ///
    /// ★ <b>サーバーは自分の到達アドレスを知らない。</b> 配信フレームに載るのは
    ///   <b>相対パス</b>だけで、authority を補うのはクライアントの責務
    ///   （→ <c>docs/protocol.md</c>）。音声（<c>/audio/…</c>）も制御 API（<c>/v1/*</c>）も
    ///   同じポートに相乗りしているので、導出は1箇所で足りる。
    /// </summary>
    public static class ServerUrl
    {
        /// <summary><c>ws://host:port</c> / <c>wss://host:port</c> → <c>http(s)://host:port</c></summary>
        public static string ToHttpBase(string serverUrl)
        {
            var uri = new Uri(serverUrl);
            var scheme = uri.Scheme == "wss" ? "https" : "http";
            return scheme + "://" + uri.Authority;
        }
    }
}
