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

        /// <summary>
        /// 探索で得た <paramref name="host"/> と <paramref name="port"/> から <c>ws://host:port</c> を組む。
        /// 組めない（ホストが空・ポートが範囲外・URL として不正）ときは <c>null</c>。
        /// IPv6 リテラルは角括弧で包む。
        /// </summary>
        public static string FromHostPort(string host, int port)
        {
            if (string.IsNullOrEmpty(host) || port < 1 || port > 65535) return null;
            var authority = host.Contains(":") && !host.StartsWith("[") ? "[" + host + "]" : host;
            var url = "ws://" + authority + ":" + port;
            return IsValid(url) ? url : null;
        }

        /// <summary>
        /// <c>ws://</c> / <c>wss://</c> の絶対 URL か。
        ///
        /// ★ スキームまで見ること。<c>Uri.TryCreate</c> は <c>http://…</c> も
        ///   <c>file:///…</c> も通すが、<c>ClientWebSocket</c> は <c>ws</c> / <c>wss</c> しか繋げない。
        /// </summary>
        public static bool IsValid(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return false;
            return parsed.Scheme == "ws" || parsed.Scheme == "wss";
        }
    }
}
