using System.Collections.Generic;
using System.Text;
using ChatterMascot.Net;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// サーバーのマニフェストとローカル <c>synced/</c> の走査結果から、取得・削除を決める計画。
    ///
    /// ★ <c>AssetSyncPlan.Build</c> は台帳ファイルを持たない——ローカルの状態はテストごとに
    ///   <see cref="Dictionary{TKey,TValue}"/> で直接渡す（実ファイルの代わり）。
    /// </summary>
    [TestFixture]
    public sealed class AssetSyncPlanTests
    {
        private const string ModelPath = "models/mascot.vrm";
        private const string IdlePath = "animations/idle.vrma";
        private const string HappyPath = "animations/happy/wave.vrma";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static string Manifest(params (string path, long size, string sha256)[] files)
        {
            var sb = new StringBuilder("{\"files\":[");
            for (var i = 0; i < files.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var f = files[i];
                sb.Append("{\"path\":\"").Append(f.path)
                  .Append("\",\"size\":").Append(f.size)
                  .Append(",\"sha256\":\"").Append(f.sha256).Append("\"}");
            }
            return sb.Append("]}").ToString();
        }

        [Test]
        public void MatchingHashFetchesNothing()
        {
            var local = new Dictionary<string, string> { [ModelPath] = HashA };
            var plan = AssetSyncPlan.Build(Manifest((ModelPath, 10, HashA)), local);

            Assert.That(plan.ManifestOk, Is.True);
            Assert.That(plan.Fetch, Is.Empty);
            Assert.That(plan.Delete, Is.Empty);
        }

        [Test]
        public void MismatchedHashIsFetched()
        {
            var local = new Dictionary<string, string> { [ModelPath] = HashA };
            var plan = AssetSyncPlan.Build(Manifest((ModelPath, 10, HashB)), local);

            Assert.That(plan.Fetch.Count, Is.EqualTo(1));
            Assert.That(plan.Fetch[0].Path, Is.EqualTo(ModelPath));
            Assert.That(plan.Fetch[0].Sha256, Is.EqualTo(HashB));
        }

        [Test]
        public void MissingLocalFileIsFetched()
        {
            var plan = AssetSyncPlan.Build(Manifest((ModelPath, 10, HashA)), new Dictionary<string, string>());

            Assert.That(plan.Fetch.Count, Is.EqualTo(1));
            Assert.That(plan.Fetch[0].Path, Is.EqualTo(ModelPath));
        }

        [Test]
        public void LocalFileNotInManifestIsDeleted()
        {
            var local = new Dictionary<string, string> { [ModelPath] = HashA, [IdlePath] = HashB };
            var plan = AssetSyncPlan.Build(Manifest((ModelPath, 10, HashA)), local);

            Assert.That(plan.Delete, Is.EqualTo(new[] { IdlePath }));
        }

        [Test]
        public void BrokenManifestIsUnreadable()
        {
            var plan = AssetSyncPlan.Build("{ぐちゃぐちゃ", new Dictionary<string, string>());

            Assert.That(plan.ManifestOk, Is.False);
        }

        /// <summary>★ 読めなかったときは Delete を1件も出さない——前回のキャッシュを消さない。</summary>
        [Test]
        public void UnreadableManifestNeverDeletesLocalFiles()
        {
            var local = new Dictionary<string, string> { [ModelPath] = HashA, [IdlePath] = HashB };
            var plan = AssetSyncPlan.Build("not json", local);

            Assert.That(plan.ManifestOk, Is.False);
            Assert.That(plan.Delete, Is.Empty);
            Assert.That(plan.Fetch, Is.Empty);
        }

        /// <summary><c>animations/&lt;category&gt;/&lt;name&gt;.vrma</c> の3形目。</summary>
        [Test]
        public void AcceptsCategorizedAnimationPaths()
        {
            var plan = AssetSyncPlan.Build(Manifest((HappyPath, 10, HashA)), new Dictionary<string, string>());

            Assert.That(plan.ManifestOk, Is.True);
            Assert.That(plan.Fetch.Count, Is.EqualTo(1));
        }

        /// <summary>
        /// ★ このパスはローカルの書き込み先とサーバーへの GET パスの両方の材料になるので、
        ///   契約の3形から外れたら（<c>..</c> を含めて）マニフェストごと読めなかったことにする。
        /// </summary>
        [Test]
        public void RejectsPathTraversal()
        {
            var plan = AssetSyncPlan.Build(Manifest(("animations/idle/..", 10, HashA)), new Dictionary<string, string>());

            Assert.That(plan.ManifestOk, Is.False);
        }

        /// <summary>
        /// ★★ ファイル名の文字集合はサーバー側（<c>core/src/core/assetPath.ts</c>）と同じにしてある。
        ///   片方だけ緩めると、配れないものを書き込み先として受け入れる／配られたものを取りこぼす、
        ///   のどちらかに倒れる。
        /// </summary>
        [TestCase("animations/happy/wave.vrma", true)]
        [TestCase("animations/happy/wave-01_a.vrma", true)]
        [TestCase("animations/happy/.hidden.vrma", false, Description = "先頭のドットは通さない")]
        [TestCase("animations/happy/wave.vrm", false, Description = "拡張子違い")]
        [TestCase("animations/happy/手を振る.vrma", false, Description = "文字集合の外")]
        [TestCase("animations/happy/wave 1.vrma", false, Description = "空白は URL でそのまま使えない")]
        [TestCase("animations/neutral/wave.vrma", false, Description = "カテゴリのディレクトリ名ではない")]
        [TestCase("animations/happy/sub/wave.vrma", false, Description = "セグメントが多い")]
        [TestCase("animations/happy/wave.vrma\n", false, Description = "★ .NET の `$` はこれを通す。`\\z` で括っていることの確認")]
        public void FileNameCharsetMatchesTheServer(string path, bool accepted)
        {
            var plan = AssetSyncPlan.Build(Manifest((path, 10, HashA)), new Dictionary<string, string>());

            Assert.That(plan.ManifestOk, Is.EqualTo(accepted), path);
        }

        /// <summary>
        /// 端末に出す1行（<c>AssetSyncClient.DescribeResult</c>）。
        /// ★ <b>何も変わらなかった起動では黙る。</b> 定常状態ではマニフェストしか流れないので、
        ///   毎回出すと「変わっていない」ことを知らせるだけの通知が起動のたびに出る。
        /// </summary>
        [Test]
        public void UnchangedSyncSaysNothing()
        {
            Assert.That(AssetSyncClient.DescribeResult(0, 0, 0), Is.Null);
        }

        [Test]
        public void FullSyncMentionsTheNextLaunch()
        {
            var message = AssetSyncClient.DescribeResult(3, 3, 0);

            Assert.That(message, Is.Not.Null);
            Assert.That(message, Does.Contain("次に起動"));
        }

        /// <summary>削除だけでも内容は変わっているので黙らない。</summary>
        [Test]
        public void DeleteOnlySyncStillSpeaks()
        {
            Assert.That(AssetSyncClient.DescribeResult(0, 0, 2), Is.Not.Null);
        }

        /// <summary>
        /// ★ 取りきれなかったことを隠さない。細い回線では1回の起動で終わらないので、
        ///   もう一度立ち上げれば続きを取ると分かる文面にする。
        /// </summary>
        [Test]
        public void PartialSyncSaysItWillContinue()
        {
            var message = AssetSyncClient.DescribeResult(1, 3, 0);

            Assert.That(message, Does.Contain("1/3"));
            Assert.That(message, Does.Contain("続き"));
        }

        /// <summary>
        /// ★★ <b>1件も取れなかったときに「更新した」と言わないこと。</b> 取りに行って全部
        ///   こぼしたのと、取れて反映を待っているのとでは、次にやることが違う——前者は
        ///   もう一度立ち上げれば最初から取り直し、後者は立ち上げれば見た目が変わる。
        /// </summary>
        [Test]
        public void FailedSyncDoesNotClaimAnUpdate()
        {
            var message = AssetSyncClient.DescribeResult(0, 2, 0);

            Assert.That(message, Is.Not.Null);
            Assert.That(message, Does.Not.Contain("更新しました"));
        }
    }
}
