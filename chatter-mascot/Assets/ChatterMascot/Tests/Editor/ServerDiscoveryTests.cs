using ChatterMascot.Net;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>探すのは Android で、設定ファイルに接続先が無く、トークンがあるときだけ。</summary>
    [TestFixture]
    public sealed class ServerDiscoveryTests
    {
        [TestCase(RuntimePlatform.Android, "", "token", true)]
        [TestCase(RuntimePlatform.Android, null, "token", true)]
        [TestCase(RuntimePlatform.Android, "ws://192.168.1.5:8570", "token", false)]
        [TestCase(RuntimePlatform.Android, "", "", false)]
        [TestCase(RuntimePlatform.Android, "", null, false)]
        [TestCase(RuntimePlatform.Android, "ws://192.168.1.5:8570", "", false)]
        [TestCase(RuntimePlatform.OSXPlayer, "", "token", false)]
        [TestCase(RuntimePlatform.OSXEditor, "", "token", false)]
        [TestCase(RuntimePlatform.WindowsPlayer, "", "token", false)]
        public void DecidesWhetherToDiscover(RuntimePlatform platform, string url, string token, bool expected)
        {
            Assert.That(ServerDiscovery.ShouldDiscover(platform, url, token), Is.EqualTo(expected));
        }
    }
}
