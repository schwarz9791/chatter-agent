using System;
using System.Collections.Generic;
using System.Linq;
using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>.vrm</c> / <c>.vrma</c> の探索順。
    ///
    /// ★ <b>ここが <c>core/src/core/paths.ts</c> と食い違うと、ユーザーから見て
    ///   「chatter-agent の設定はここ1箇所」が崩れる。</b> 特に
    ///   <c>XDG_CONFIG_HOME</c> の空文字の扱いは JS の <c>||</c> に合わせること。
    /// </summary>
    [TestFixture]
    public sealed class AssetPathTests
    {
        private const string Home = "/home/u";

        private static AssetEnv Env(
            IReadOnlyList<string> commandLine = null,
            IDictionary<string, string> variables = null,
            bool desktop = true,
            bool windows = false,
            Func<string, string, IReadOnlyList<string>> listFiles = null,
            string persistentDataPath = "/persist",
            string streamingAssetsPath = "/streaming",
            string homeDirectory = Home)
        {
            return new AssetEnv
            {
                CommandLine = commandLine ?? Array.Empty<string>(),
                Variables = new Dictionary<string, string>(
                    variables ?? new Dictionary<string, string>(), StringComparer.Ordinal),
                PersistentDataPath = persistentDataPath,
                StreamingAssetsPath = streamingAssetsPath,
                HomeDirectory = homeDirectory,
                IsWindows = windows,
                HasUserConfigDirectory = desktop,
                ListFiles = listFiles ?? ((_, __) => Array.Empty<string>()),
            };
        }

        private static string[] Paths(AssetEnv env, AssetKind kind) =>
            AssetPath.Enumerate(env, kind).Select(c => c.Path).ToArray();

        [Test]
        public void BundledModelIsAlwaysTheLastResort()
        {
            Assert.That(Paths(Env(), AssetKind.Vrm),
                Is.EqualTo(new[] { "/persist/model.vrm", "/streaming/vita.vrm" }));
        }

        [Test]
        public void VrmaUsesItsOwnNames()
        {
            Assert.That(Paths(Env(), AssetKind.Vrma),
                Is.EqualTo(new[] { "/persist/idle.vrma", "/streaming/idle_loop.vrma" }));
        }

        [Test]
        public void CommandLineWinsOverEverything()
        {
            var env = Env(
                commandLine: new[] { "app", "-serverUrl", "ws://x", "-vrm", "/tmp/a.vrm" },
                variables: new Dictionary<string, string> { { "CHATTER_MASCOT_VRM", "/tmp/b.vrm" } });

            Assert.That(Paths(env, AssetKind.Vrm)[0], Is.EqualTo("/tmp/a.vrm"));
            Assert.That(Paths(env, AssetKind.Vrm)[1], Is.EqualTo("/tmp/b.vrm"));
        }

        [Test]
        public void ArgumentAtTheEndHasNoValue()
        {
            var env = Env(commandLine: new[] { "app", "-vrm" });
            Assert.That(Paths(env, AssetKind.Vrm), Is.EqualTo(new[] { "/persist/model.vrm", "/streaming/vita.vrm" }));
        }

        /// <summary>
        /// ★ <b>TS 側は <c>e.env.XDG_CONFIG_HOME || join(homedir, ".config")</c>。</b>
        ///   JS の <c>||</c> は <c>""</c> も falsy なので、C# も
        ///   <c>string.IsNullOrEmpty</c> でなければならない。
        /// </summary>
        [Test]
        public void EmptyXdgConfigHomeFallsBackToDotConfig()
        {
            var env = Env(variables: new Dictionary<string, string> { { "XDG_CONFIG_HOME", "" } });
            Assert.That(AssetPath.RuntimeDirectory(env), Is.EqualTo("/home/u/.config/chatter-agent"));
        }

        [Test]
        public void XdgConfigHomeIsHonouredWhenSet()
        {
            var env = Env(variables: new Dictionary<string, string> { { "XDG_CONFIG_HOME", "/xdg" } });
            Assert.That(AssetPath.RuntimeDirectory(env), Is.EqualTo("/xdg/chatter-agent"));
        }

        [Test]
        public void WindowsUsesAppData()
        {
            var env = Env(windows: true,
                variables: new Dictionary<string, string> { { "APPDATA", "C:/Users/u/AppData/Roaming" } });
            Assert.That(AssetPath.RuntimeDirectory(env),
                Is.EqualTo("C:/Users/u/AppData/Roaming/chatter-agent"));
        }

        [Test]
        public void WindowsFallsBackWhenAppDataIsMissing()
        {
            Assert.That(AssetPath.RuntimeDirectory(Env(windows: true)),
                Is.EqualTo("/home/u/AppData/Roaming/chatter-agent"));
        }

        /// <summary>★ 既定の比較はカルチャ依存。マシンで「どのモデルが出るか」が変わる。</summary>
        [Test]
        public void UserModelsAreOrdinalSorted()
        {
            var dir = "/home/u/.config/chatter-agent/models";
            var env = Env(listFiles: (d, pattern) => d == dir && pattern == "*.vrm"
                ? new[] { dir + "/b.vrm", dir + "/A.vrm", dir + "/a.vrm" }
                : Array.Empty<string>());

            Assert.That(Paths(env, AssetKind.Vrm), Is.EqualTo(new[]
            {
                "/persist/model.vrm",
                dir + "/A.vrm",   // Ordinal では大文字が先
                dir + "/a.vrm",
                dir + "/b.vrm",
                "/streaming/vita.vrm",
            }));
        }

        /// <summary>★ Android には共有ファイルシステムが無い。</summary>
        [Test]
        public void UserConfigIsSkippedWithoutSharedFileSystem()
        {
            var dir = "/home/u/.config/chatter-agent/models";
            var env = Env(desktop: false, listFiles: (_, __) => new[] { dir + "/a.vrm" });

            Assert.That(Paths(env, AssetKind.Vrm),
                Is.EqualTo(new[] { "/persist/model.vrm", "/streaming/vita.vrm" }));
        }

        [Test]
        public void VrmaLooksInAnimationsDirectory()
        {
            var seen = new List<string>();
            var env = Env(listFiles: (d, pattern) =>
            {
                seen.Add(d + " " + pattern);
                return Array.Empty<string>();
            });

            AssetPath.Enumerate(env, AssetKind.Vrma);
            Assert.That(seen, Is.EqualTo(new[] { "/home/u/.config/chatter-agent/animations *.vrma" }));
        }

        [Test]
        public void TildeIsExpanded()
        {
            var env = Env(variables: new Dictionary<string, string> { { "CHATTER_MASCOT_VRM", "~/models/x.vrm" } });
            Assert.That(Paths(env, AssetKind.Vrm)[0], Is.EqualTo("/home/u/models/x.vrm"));
        }

        [Test]
        public void SourcesAreReportedForLogging()
        {
            var env = Env(commandLine: new[] { "app", "-vrm", "/tmp/a.vrm" });
            var candidates = AssetPath.Enumerate(env, AssetKind.Vrm);

            Assert.That(candidates[0].Source, Is.EqualTo(AssetSource.CommandLine));
            Assert.That(candidates[^1].Source, Is.EqualTo(AssetSource.StreamingAssets));
        }

        /// <summary>★ 読めないディレクトリで探索順が丸ごと壊れないこと。</summary>
        [Test]
        public void NullFromListFilesIsTolerated()
        {
            var env = Env(listFiles: (_, __) => null);
            Assert.That(Paths(env, AssetKind.Vrm),
                Is.EqualTo(new[] { "/persist/model.vrm", "/streaming/vita.vrm" }));
        }

        /// <summary>
        /// ★ <c>persistentDataPath</c> が空のとき、以前の <c>Join</c> は左辺の空を
        ///   右辺の返却で吸ってしまい、相対パス <c>"model.vrm"</c> がそのまま候補になっていた。
        ///   <c>File.Exists</c> はカレントディレクトリ（Unity ではプロジェクトルート）基準で
        ///   評価されるので、プロジェクトルートに <c>model.vrm</c> を置くだけで
        ///   <b>同梱より上位で誤って一致する</b>（PR #69 の再レビューで判明）。
        ///   ここでは <see cref="AssetCandidate.Source"/> で「<c>PersistentData</c> の候補が
        ///   そもそも積まれないこと」を固定する。
        /// </summary>
        [Test]
        public void EmptyPersistentDataPathProducesNoCandidate()
        {
            var env = Env(persistentDataPath: "");
            var candidates = AssetPath.Enumerate(env, AssetKind.Vrm);

            Assert.That(candidates.Any(c => c.Source == AssetSource.PersistentData), Is.False);
        }

        /// <summary>
        /// ★ 上と同じ穴が <c>streamingAssetsPath</c> 側にもあった。空だと相対パス
        ///   <c>"vita.vrm"</c> が候補になり、カレントディレクトリ次第で同梱と別のファイルを
        ///   拾ってしまう。<c>StreamingAssets</c> の候補が積まれないことを固定する。
        /// </summary>
        [Test]
        public void EmptyStreamingAssetsPathProducesNoCandidate()
        {
            var env = Env(streamingAssetsPath: "");
            var candidates = AssetPath.Enumerate(env, AssetKind.Vrm);

            Assert.That(candidates.Any(c => c.Source == AssetSource.StreamingAssets), Is.False);
        }

        /// <summary>
        /// ★ <c>homeDirectory</c> が空かつ <c>XDG_CONFIG_HOME</c> 未設定だと、
        ///   <c>RuntimeDirectory</c> の基準が空になる。以前はここも相対パス
        ///   <c>".config/chatter-agent"</c> に化けていた。<c>listFiles</c> に
        ///   例外を投げる実装を注入し、<b>そもそも呼ばれないこと</b>まで確認する
        ///   （呼ばれてしまうと、注入された実装が <c>null</c>/相対パスをどう扱うかに
        ///   結果が左右される）。
        /// </summary>
        [Test]
        public void EmptyHomeDirectoryProducesNoUserConfigCandidateOnDesktop()
        {
            var env = Env(homeDirectory: "",
                listFiles: (_, __) => throw new InvalidOperationException("呼ばれないはず"));
            var candidates = AssetPath.Enumerate(env, AssetKind.Vrm);

            Assert.That(candidates.Any(c => c.Source == AssetSource.UserConfig), Is.False);
            Assert.That(AssetPath.RuntimeDirectory(env), Is.Null.Or.Empty);
        }

        /// <summary>
        /// ★ <c>~/</c> 展開も同じ穴を持っていた。<c>homeDirectory</c> が空だと
        ///   <c>-vrm ~/x.vrm</c> の <c>~/</c> が消えて相対パス <c>"x.vrm"</c> になり、
        ///   カレントディレクトリ次第で意図しないファイルを読んでしまう。
        ///   起動引数の候補がそもそも積まれないことを固定する。
        /// </summary>
        [Test]
        public void EmptyHomeDirectoryProducesNoCandidateForTildeExpansion()
        {
            var env = Env(homeDirectory: "", commandLine: new[] { "app", "-vrm", "~/x.vrm" });
            var candidates = AssetPath.Enumerate(env, AssetKind.Vrm);

            Assert.That(candidates.Any(c => c.Source == AssetSource.CommandLine), Is.False);
        }
    }
}
