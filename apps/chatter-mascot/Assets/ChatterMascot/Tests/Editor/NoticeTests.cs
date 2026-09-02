using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>StreamingAssets/NOTICE.txt</c> がリポジトリの <c>NOTICE</c> と一致していること。
    ///
    /// ★★ <b>これが「同じ内容を2箇所に置いてよい」唯一の根拠。</b> 設定パネルの
    ///   「このアプリについて」は同梱されたコピーを読む（<c>.app</c> の中からは
    ///   リポジトリの <c>NOTICE</c> が見えない）。手で二重管理すると必ずズレるので、
    ///   ズレたらここが落ちるようにしてある。
    ///
    /// ★ 落ちたら <b>リポジトリの <c>NOTICE</c> に合わせてコピーを更新する</b>（逆ではない）:
    ///   <c>cp NOTICE apps/chatter-mascot/Assets/StreamingAssets/NOTICE.txt</c>
    ///
    /// ★ ビルド時のコピーにしていない理由: ビルド後処理は失敗しても
    ///   ビルドを落とさない方針（→ <c>MacPostBuild</c>）なので、
    ///   <b>黙って古いライセンスが同梱される</b>経路ができてしまう。
    ///   コミット済みのファイル + テストなら、CI でも手元でも同じところで気づける。
    /// </summary>
    [TestFixture]
    public sealed class NoticeTests
    {
        [Test]
        public void StreamingAssetsCopyMatchesTheRepositoryNotice()
        {
            // Assets/ → apps/chatter-mascot/ → apps/ → リポジトリのルート
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
            var source = Path.Combine(root, "NOTICE");
            var copy = Path.Combine(Application.dataPath, "StreamingAssets", "NOTICE.txt");

            Assert.That(File.Exists(source), Is.True, $"リポジトリの NOTICE が見つかりません: {source}");
            Assert.That(
                File.Exists(copy), Is.True,
                $"同梱の NOTICE.txt がありません: {copy}。cp NOTICE {copy} で作ること");

            Assert.That(
                File.ReadAllText(copy), Is.EqualTo(File.ReadAllText(source)),
                "同梱の NOTICE.txt がリポジトリの NOTICE と違います。" +
                "cp NOTICE apps/chatter-mascot/Assets/StreamingAssets/NOTICE.txt で合わせること");
        }
    }
}
