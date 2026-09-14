using System;
using System.Collections.Generic;
using ChatterMascot.Settings;
using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>settings.json</c> の置き場所。<c>AssetPathTests</c> と同じ流儀で
    /// <see cref="AssetEnv"/> を組み立てて確かめる。
    /// </summary>
    [TestFixture]
    public sealed class SettingsLocationTests
    {
        private const string Home = "/home/u";

        private static AssetEnv Env(
            bool desktop = true,
            bool windows = false,
            IDictionary<string, string> variables = null,
            string persistentDataPath = "/persist",
            string homeDirectory = Home)
        {
            return new AssetEnv
            {
                CommandLine = Array.Empty<string>(),
                Variables = new Dictionary<string, string>(
                    variables ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                PersistentDataPath = persistentDataPath,
                StreamingAssetsPath = "/streaming",
                HomeDirectory = homeDirectory,
                IsWindows = windows,
                HasUserConfigDirectory = desktop,
                ListFiles = (_, __) => Array.Empty<string>(),
            };
        }

        [Test]
        public void DesktopUsesTheSharedRuntimeDirectory()
        {
            Assert.That(SettingsLocation.Resolve(Env()),
                Is.EqualTo("/home/u/.config/chatter-agent/mascot/settings.json"));
        }

        [Test]
        public void DesktopHonoursXdgConfigHome()
        {
            var env = Env(variables: new Dictionary<string, string> { { "XDG_CONFIG_HOME", "/xdg" } });
            Assert.That(SettingsLocation.Resolve(env), Is.EqualTo("/xdg/chatter-agent/mascot/settings.json"));
        }

        /// <summary>★ Android には共有ファイルシステムが無いので、<c>persistentDataPath</c> 直下に置く</summary>
        [Test]
        public void WithoutASharedFileSystemUsesPersistentDataPath()
        {
            var env = Env(desktop: false, persistentDataPath: "/data/user/0/tech.sukima.chattermascot/files");
            Assert.That(SettingsLocation.Resolve(env),
                Is.EqualTo("/data/user/0/tech.sukima.chattermascot/files/settings.json"));
        }

        [Test]
        public void ResolvesNothingForANullEnv()
        {
            Assert.That(SettingsLocation.Resolve(null), Is.Null);
        }

        /// <summary>★ 基準（<c>homeDirectory</c>・<c>XDG_CONFIG_HOME</c>）が無ければ解決できない</summary>
        [Test]
        public void UnresolvableWhenTheDesktopRuntimeDirectoryHasNoBase()
        {
            var env = Env(homeDirectory: "");
            Assert.That(SettingsLocation.Resolve(env), Is.Null.Or.Empty);
        }

        [Test]
        public void UnresolvableWithoutAPersistentDataPath()
        {
            var env = Env(desktop: false, persistentDataPath: "");
            Assert.That(SettingsLocation.Resolve(env), Is.Null);
        }
    }
}
