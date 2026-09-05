using System;
using System.Collections.Generic;
using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="AnimationManifest"/>。<see cref="AssetPath.AnimationRoots"/> の各ルート ×
    /// 6カテゴリを舐めて、どの <c>.vrma</c> を採用するかを決める合成ロジック。
    ///
    /// ★ VRM10 に依存する実際の読み込み（<c>Vrm/VrmMotionPlayer</c>）はここからは見えないので、
    ///   ここでは「<c>ListFiles</c> が返した文字列からどの <see cref="MotionClip"/> の集合が
    ///   組み上がるか」だけを固定する。
    /// </summary>
    [TestFixture]
    public sealed class AnimationManifestTests
    {
        private const string Home = "/home/u";
        private const string UserAnimations = "/home/u/.config/chatter-agent/animations";

        /// <summary>
        /// <c>AssetPathTests.Env</c> と同じ流儀。<c>ListFiles</c> は
        /// <c>(dir, pattern) =&gt; files[dir]</c> の形で注入する。
        /// </summary>
        private static AssetEnv Env(
            Dictionary<string, string[]> files = null,
            bool desktop = true,
            string persistentDataPath = "/persist",
            string streamingAssetsPath = "/streaming",
            string homeDirectory = Home)
        {
            var map = files ?? new Dictionary<string, string[]>();
            return new AssetEnv
            {
                PersistentDataPath = persistentDataPath,
                StreamingAssetsPath = streamingAssetsPath,
                HomeDirectory = homeDirectory,
                HasUserConfigDirectory = desktop,
                ListFiles = (dir, pattern) => map.TryGetValue(dir, out var found) ? found : Array.Empty<string>(),
            };
        }

        [Test]
        public void ScansAllSixCategories()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/idle"] = new[] { "/persist/animations/idle/a.vrma" },
                ["/persist/animations/happy"] = new[] { "/persist/animations/happy/b.vrma" },
                ["/persist/animations/angry"] = new[] { "/persist/animations/angry/c.vrma" },
                ["/persist/animations/sad"] = new[] { "/persist/animations/sad/d.vrma" },
                ["/persist/animations/relaxed"] = new[] { "/persist/animations/relaxed/e.vrma" },
                ["/persist/animations/surprised"] = new[] { "/persist/animations/surprised/f.vrma" },
            };
            var manifest = AnimationManifest.Build(Env(files));

            Assert.That(manifest.Count(MotionCategory.Idle), Is.EqualTo(1));
            Assert.That(manifest.Count(MotionCategory.Happy), Is.EqualTo(1));
            Assert.That(manifest.Count(MotionCategory.Angry), Is.EqualTo(1));
            Assert.That(manifest.Count(MotionCategory.Sad), Is.EqualTo(1));
            Assert.That(manifest.Count(MotionCategory.Relaxed), Is.EqualTo(1));
            Assert.That(manifest.Count(MotionCategory.Surprised), Is.EqualTo(1));
            Assert.That(manifest.TotalCount, Is.EqualTo(6));
        }

        [Test]
        public void EmptyEnvironmentProducesAnEmptyManifestForEveryCategory()
        {
            var manifest = AnimationManifest.Build(Env());

            foreach (var category in MotionCategories.All)
            {
                Assert.That(manifest.Count(category), Is.EqualTo(0), category.ToString());
                Assert.That(manifest.Clips(category), Is.Empty, category.ToString());
            }
            Assert.That(manifest.TotalCount, Is.EqualTo(0));
        }

        /// <summary>
        /// ルートの優先順は <c>persistentDataPath</c> → ユーザー設定 → 同梱
        /// （<see cref="AssetPath.AnimationRoots"/>）。同じルート内では <c>Ordinal</c> ソート。
        /// </summary>
        [Test]
        public void ClipsAppearInRootPriorityOrder()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/idle"] = new[] { "/persist/animations/idle/z.vrma" },
                [UserAnimations + "/idle"] = new[] { UserAnimations + "/idle/a.vrma" },
                ["/streaming/animations/idle"] = new[] { "/streaming/animations/idle/m.vrma" },
            };
            var manifest = AnimationManifest.Build(Env(files));
            var clips = manifest.Clips(MotionCategory.Idle);

            Assert.That(clips.Count, Is.EqualTo(3));
            Assert.That(clips[0].Path, Is.EqualTo("/persist/animations/idle/z.vrma"), "persist が先");
            Assert.That(clips[1].Path, Is.EqualTo(UserAnimations + "/idle/a.vrma"), "ユーザーが次");
            Assert.That(clips[2].Path, Is.EqualTo("/streaming/animations/idle/m.vrma"), "同梱が最後");
        }

        /// <summary>★ 既定の比較はカルチャ依存。マシンで採用順が変わらないよう Ordinal で揃える。</summary>
        [Test]
        public void SameRootFilesAreOrdinalSorted()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/happy"] = new[]
                {
                    "/persist/animations/happy/b.vrma",
                    "/persist/animations/happy/A.vrma",
                    "/persist/animations/happy/a.vrma",
                },
            };
            var manifest = AnimationManifest.Build(Env(files));
            var names = new List<string>();
            foreach (var clip in manifest.Clips(MotionCategory.Happy)) names.Add(clip.FileName);

            Assert.That(names, Is.EqualTo(new[] { "A.vrma", "a.vrma", "b.vrma" }), "Ordinal では大文字が先");
        }

        /// <summary>
        /// ★★ <b>同じ <c>FileName</c> は先のルートが勝つ。</b> 後のルートで見つかった同名は捨てる
        /// （ユーザー拡張が同梱の同名ファイルを覆せるように）。
        /// </summary>
        [Test]
        public void EarlierRootWinsForTheSameFileName()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/idle"] = new[] { "/persist/animations/idle/x.vrma" },
                [UserAnimations + "/idle"] = new[] { UserAnimations + "/idle/x.vrma" },
                ["/streaming/animations/idle"] = new[] { "/streaming/animations/idle/x.vrma" },
            };
            var manifest = AnimationManifest.Build(Env(files));
            var clips = manifest.Clips(MotionCategory.Idle);

            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Path, Is.EqualTo("/persist/animations/idle/x.vrma"));
        }

        [Test]
        public void ClassifiesCoolAndCuteSuffixesAndDefaultsToNatural()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/happy"] = new[]
                {
                    "/persist/animations/happy/Foo__cool.vrma",
                    "/persist/animations/happy/Bar__cute.vrma",
                    "/persist/animations/happy/Baz.vrma",
                },
            };
            var manifest = AnimationManifest.Build(Env(files));
            var byName = new Dictionary<string, MotionClip>();
            foreach (var clip in manifest.Clips(MotionCategory.Happy)) byName[clip.FileName] = clip;

            Assert.That(byName["Foo__cool.vrma"].Style, Is.EqualTo(MotionStyle.Cool));
            Assert.That(byName["Bar__cute.vrma"].Style, Is.EqualTo(MotionStyle.Cute));
            Assert.That(byName["Baz.vrma"].Style, Is.EqualTo(MotionStyle.Natural));
        }

        /// <summary>
        /// ★ <c>bundled</c> は最後のルート（同梱）の後ろにマージされる。同名なら走査結果が勝つ
        /// （<see cref="EarlierRootWinsForTheSameFileName"/> と同じ規則）。
        /// </summary>
        [Test]
        public void BundledClipsAreAppendedAfterScannedOnes()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/idle"] = new[] { "/persist/animations/idle/scanned.vrma" },
            };
            var bundled = new[]
            {
                new MotionClip(MotionCategory.Idle, "bundle://idle/scanned.vrma", "scanned.vrma", MotionStyle.Natural),
                new MotionClip(MotionCategory.Idle, "bundle://idle/extra.vrma", "extra.vrma", MotionStyle.Natural),
            };

            var manifest = AnimationManifest.Build(Env(files), bundled);
            var clips = manifest.Clips(MotionCategory.Idle);

            Assert.That(clips.Count, Is.EqualTo(2), "scanned.vrma は同名なので bundled 側は捨てられる");
            Assert.That(clips[0].Path, Is.EqualTo("/persist/animations/idle/scanned.vrma"), "走査結果が勝つ");
            Assert.That(clips[1].Path, Is.EqualTo("bundle://idle/extra.vrma"), "bundled は最後に足される");
        }

        [Test]
        public void PicksTheFirstClipWhenRandomIsZero()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/happy"] = new[]
                {
                    "/persist/animations/happy/a.vrma",
                    "/persist/animations/happy/b.vrma",
                    "/persist/animations/happy/c.vrma",
                },
            };
            var manifest = AnimationManifest.Build(Env(files));

            var picked = manifest.Pick(MotionCategory.Happy, () => 0.0);
            Assert.That(picked.FileName, Is.EqualTo("a.vrma"));
        }

        [Test]
        public void PicksTheLastClipWhenRandomIsJustBelowOne()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/happy"] = new[]
                {
                    "/persist/animations/happy/a.vrma",
                    "/persist/animations/happy/b.vrma",
                    "/persist/animations/happy/c.vrma",
                },
            };
            var manifest = AnimationManifest.Build(Env(files));

            var picked = manifest.Pick(MotionCategory.Happy, () => 0.999);
            Assert.That(picked.FileName, Is.EqualTo("c.vrma"));
        }

        /// <summary>
        /// ★ 契約は <c>[0,1)</c> だが、境界を破って <c>1.0</c> が来ても配列外参照にならないこと
        ///   （<c>count-1</c> でクランプする保険）。
        /// </summary>
        [Test]
        public void ClampsWhenRandomReturnsOne()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/happy"] = new[]
                {
                    "/persist/animations/happy/a.vrma",
                    "/persist/animations/happy/b.vrma",
                },
            };
            var manifest = AnimationManifest.Build(Env(files));

            Assert.DoesNotThrow(() =>
            {
                var picked = manifest.Pick(MotionCategory.Happy, () => 1.0);
                Assert.That(picked.FileName, Is.EqualTo("b.vrma"));
            });
        }

        [Test]
        public void PickReturnsNullForAnEmptyCategory()
        {
            var manifest = AnimationManifest.Build(Env());
            Assert.That(manifest.Pick(MotionCategory.Surprised, () => 0.0), Is.Null);
        }

        /// <summary>
        /// ★★ #70 レビュー #2。<see cref="VrmMotionPlayer.Loaded"/> はこれで組む——
        /// 渡した並び順をカテゴリごとに保ったまま仕分けることを固定する。
        /// </summary>
        [Test]
        public void FromClipsGroupsByCategoryAndPreservesOrder()
        {
            var happy1 = new MotionClip(MotionCategory.Happy, "/a/happy1.vrma", "happy1.vrma", MotionStyle.Natural);
            var happy2 = new MotionClip(MotionCategory.Happy, "/a/happy2.vrma", "happy2.vrma", MotionStyle.Natural);
            var sad1 = new MotionClip(MotionCategory.Sad, "/a/sad1.vrma", "sad1.vrma", MotionStyle.Natural);

            var manifest = AnimationManifest.FromClips(new[] { happy1, sad1, happy2 });

            var happyClips = manifest.Clips(MotionCategory.Happy);
            Assert.That(happyClips.Count, Is.EqualTo(2));
            Assert.That(happyClips[0], Is.SameAs(happy1));
            Assert.That(happyClips[1], Is.SameAs(happy2));
            Assert.That(manifest.Clips(MotionCategory.Sad).Count, Is.EqualTo(1));
            Assert.That(manifest.TotalCount, Is.EqualTo(3));
        }

        [Test]
        public void FromClipsOfAnEmptySequenceHasZeroTotalCount()
        {
            var manifest = AnimationManifest.FromClips(Array.Empty<MotionClip>());

            foreach (var category in MotionCategories.All)
            {
                Assert.That(manifest.Clips(category), Is.Empty, category.ToString());
            }
            Assert.That(manifest.TotalCount, Is.EqualTo(0));
        }

        [Test]
        public void DescribeListsAllCategoriesWithCounts()
        {
            var files = new Dictionary<string, string[]>
            {
                ["/persist/animations/idle"] = new[]
                {
                    "/persist/animations/idle/1.vrma",
                    "/persist/animations/idle/2.vrma",
                },
                ["/persist/animations/happy"] = new[] { "/persist/animations/happy/1.vrma" },
            };
            var manifest = AnimationManifest.Build(Env(files));

            Assert.That(manifest.Describe(),
                Is.EqualTo("idle=2 happy=1 angry=0 sad=0 relaxed=0 surprised=0"));
        }
    }
}
