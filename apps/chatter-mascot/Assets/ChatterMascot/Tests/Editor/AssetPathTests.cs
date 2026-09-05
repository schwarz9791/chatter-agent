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

        // ── 設定パネルが置いたモデル（#76） ────────────────────

        /// <summary>
        /// ★★ <c>models/*.vrm</c> の走査は <c>Ordinal</c> の先頭が勝つので、
        ///   パネルが置いたモデルを<b>名指しで先に出す</b>。これが無いと
        ///   2つ目のモデルを選んでも反映されない。
        /// </summary>
        [Test]
        public void PutsTheChosenModelBeforeTheDirectoryScan()
        {
            var env = Env(listFiles: (dir, pattern) => new[]
            {
                "/home/u/.config/chatter-agent/models/aaa.vrm",
                "/home/u/.config/chatter-agent/models/" + AssetPath.SelectedVrmFile,
            });

            var candidates = AssetPath.Enumerate(env, AssetKind.Vrm).ToList();
            var settings = candidates.FindIndex(c => c.Source == AssetSource.Settings);
            var scanned = candidates.FindIndex(c => c.Source == AssetSource.UserConfig);

            Assert.That(settings, Is.GreaterThanOrEqualTo(0), "設定の候補が出ていない");
            Assert.That(scanned, Is.GreaterThanOrEqualTo(0), "走査の候補が出ていない");
            Assert.That(settings, Is.LessThan(scanned));
        }

        /// <summary>
        /// ★★ <b>固定名であること。</b> 選んだファイルの名前をそのまま使うと
        ///   <c>models/</c> に積み上がる（実機で報告された）。
        /// </summary>
        [Test]
        public void LooksForTheChosenModelUnderAFixedName()
        {
            var env = Env(listFiles: (dir, pattern) => new[]
            {
                "/home/u/.config/chatter-agent/models/aaa.vrm",
                "/home/u/.config/chatter-agent/models/" + AssetPath.SelectedVrmFile,
            });

            var settings = AssetPath.Enumerate(env, AssetKind.Vrm).ToList()
                .Find(c => c.Source == AssetSource.Settings);

            Assert.That(settings.Path, Does.EndWith("models/" + AssetPath.SelectedVrmFile));
        }

        /// <summary>
        /// ★ <b>引き上げたぶんを走査側に二重で出さないこと。</b> 同じファイルが2回並ぶと、
        ///   ログの「どれを採ってどれを無視したか」が読めなくなる。
        /// </summary>
        [Test]
        public void DoesNotListTheChosenModelTwice()
        {
            var env = Env(listFiles: (dir, pattern) => new[]
            {
                "/home/u/.config/chatter-agent/models/" + AssetPath.SelectedVrmFile,
            });

            var paths = AssetPath.Enumerate(env, AssetKind.Vrm).ToList()
                .FindAll(c => c.Path.EndsWith(AssetPath.SelectedVrmFile, System.StringComparison.Ordinal));

            Assert.That(paths.Count, Is.EqualTo(1));
        }

        /// <summary>
        /// ★★ <b>設定を見ないこと。</b> 以前は settings.json のファイル名を渡す作りで、
        ///   <b>本番コードが誰も渡していなかった</b>ため候補が一度も出なかった。
        ///   置き場所が固定なら、走査結果に居るかどうかだけで決まる。
        /// </summary>
        [Test]
        public void FindsTheChosenModelWithoutAnySettings()
        {
            var env = Env(listFiles: (dir, pattern) => new[]
            {
                "/home/u/.config/chatter-agent/models/" + AssetPath.SelectedVrmFile,
            });

            Assert.That(
                AssetPath.Enumerate(env, AssetKind.Vrm).ToList().Exists(c => c.Source == AssetSource.Settings),
                Is.True);
        }

        /// <summary>
        /// ★ <b>置かれていなければ候補を出さない。</b> 無条件に足すと、パネルで一度も
        ///   選んでいない人の起動ログに毎回「読めませんでした」が1本増える。
        /// </summary>
        [Test]
        public void OmitsTheChosenModelWhenItIsNotThere()
        {
            Assert.That(
                AssetPath.Enumerate(Env(), AssetKind.Vrm).ToList().Exists(c => c.Source == AssetSource.Settings),
                Is.False);
        }

        /// <summary>
        /// ★ <c>-vrm</c> は切り分けの逃げ道（「設定が壊れていてもこれを付ければ必ず出る」）
        ///   なので、パネルが置いたモデルより優先を保つ。
        /// </summary>
        [Test]
        public void KeepsTheCommandLineAboveTheChosenModel()
        {
            var env = Env(
                commandLine: new[] { "-vrm", "/tmp/forced.vrm" },
                listFiles: (dir, pattern) => new[]
                {
                    "/home/u/.config/chatter-agent/models/" + AssetPath.SelectedVrmFile,
                });

            Assert.That(AssetPath.Enumerate(env, AssetKind.Vrm)[0].Source, Is.EqualTo(AssetSource.CommandLine));
        }

        /// <summary>★ Android には共有ファイルシステムが無いので、この段ごと落ちる</summary>
        [Test]
        public void OmitsTheChosenModelWithoutAUserConfigDirectory()
        {
            var env = Env(desktop: false, listFiles: (dir, pattern) => new[]
            {
                "/home/u/.config/chatter-agent/models/" + AssetPath.SelectedVrmFile,
            });

            Assert.That(
                AssetPath.Enumerate(env, AssetKind.Vrm).ToList().Exists(c => c.Source == AssetSource.Settings),
                Is.False);
        }

        /// <summary>★ <c>.vrma</c> には対応する仕組みが無い（意図的な非対称）</summary>
        [Test]
        public void DoesNotApplyTheChosenModelToAnimations()
        {
            var env = Env(listFiles: (dir, pattern) => new[]
            {
                "/home/u/.config/chatter-agent/animations/" + AssetPath.SelectedVrmFile,
            });

            Assert.That(
                AssetPath.Enumerate(env, AssetKind.Vrma).ToList().Exists(c => c.Source == AssetSource.Settings),
                Is.False);
        }

        // ── AnimationRoots（#70。AnimationManifest のルート探索） ────────────────────

        /// <summary>
        /// <see cref="AssetPath.Enumerate"/> と違い、ここは「ファイル1本」ではなく
        /// 「カテゴリ別ディレクトリの親」を返す。順序は persistentDataPath → ユーザー設定 → 同梱。
        /// </summary>
        [Test]
        public void AnimationRootsAreOrderedPersistentThenUserThenBundled()
        {
            var env = Env();
            Assert.That(AssetPath.AnimationRoots(env), Is.EqualTo(new[]
            {
                "/persist/animations",
                "/home/u/.config/chatter-agent/animations",
                "/streaming/animations",
            }));
        }

        /// <summary>★ Android には共有ファイルシステムが無いので、ユーザー段が丸ごと落ちる。</summary>
        [Test]
        public void AnimationRootsDropTheUserStepWithoutASharedFileSystem()
        {
            var env = Env(desktop: false);
            Assert.That(AssetPath.AnimationRoots(env), Is.EqualTo(new[]
            {
                "/persist/animations",
                "/streaming/animations",
            }));
        }

        /// <summary>★ .vrm / .vrma の探索と同じ規則で XDG_CONFIG_HOME が効くこと。</summary>
        [Test]
        public void AnimationRootsHonourXdgConfigHome()
        {
            var env = Env(variables: new Dictionary<string, string> { { "XDG_CONFIG_HOME", "/xdg" } });
            Assert.That(AssetPath.AnimationRoots(env), Is.EqualTo(new[]
            {
                "/persist/animations",
                "/xdg/chatter-agent/animations",
                "/streaming/animations",
            }));
        }

        /// <summary>★ <c>env == null</c> は空（<see cref="AssetPath.Enumerate"/> と同じ契約）。</summary>
        [Test]
        public void AnimationRootsIsEmptyForNullEnv()
        {
            Assert.That(AssetPath.AnimationRoots(null), Is.Empty);
        }
    }
}
