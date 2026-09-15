using ChatterMascot.Net;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>ws://</c> / <c>wss://</c> の検証と、そこから音声 / 制御 API の <c>http(s)://</c> を導く写像。
    /// </summary>
    [TestFixture]
    public sealed class ServerUrlTests
    {
        [TestCase("ws://127.0.0.1:8570")]
        [TestCase("wss://mascot.example:443")]
        [TestCase("ws://192.168.1.5:8570")]
        public void AcceptsWsAndWss(string url)
        {
            Assert.That(ServerUrl.IsValid(url), Is.True);
        }

        /// <summary>★ スキームまで見ること。<c>http</c> や <c>file</c> は通すが、<c>ClientWebSocket</c> は繋げない</summary>
        [TestCase("http://127.0.0.1:8570")]
        [TestCase("file:///etc/passwd")]
        [TestCase("127.0.0.1:8570")]
        [TestCase("")]
        [TestCase(null)]
        public void RejectsEverythingElse(string url)
        {
            Assert.That(ServerUrl.IsValid(url), Is.False);
        }

        [Test]
        public void DerivesHttpFromWs()
        {
            Assert.That(ServerUrl.ToHttpBase("ws://127.0.0.1:8570"), Is.EqualTo("http://127.0.0.1:8570"));
        }

        /// <summary>★ 既定ポート（443）は <c>Uri.Authority</c> が省くので、既定外のポートで確かめる</summary>
        [Test]
        public void DerivesHttpsFromWss()
        {
            Assert.That(ServerUrl.ToHttpBase("wss://mascot.example:8570"), Is.EqualTo("https://mascot.example:8570"));
        }
    }
}
