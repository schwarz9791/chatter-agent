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
    ///   <c>cp NOTICE chatter-mascot/Assets/StreamingAssets/NOTICE.txt</c>
    ///
    /// ★ <b>コピーで揃えるのではなく、一致を確かめて落とす。</b> ビルド後処理は失敗しても
    ///   ビルドを落とさない方針（→ <c>MacPostBuild</c>）なので、そこにコピーを置くと
    ///   <b>黙って古いライセンスが同梱される</b>経路ができる。ビルドが追跡ファイルを
    ///   書き換える形も、中断やクラッシュで食い違ったまま残る
    ///   （→ <c>ProjectSettings/AudioManager.asset</c>）。
    ///
    /// ★ <b>このテストだけが関門ではない。</b> 出荷の入口は <c>scripts/unity.sh</c> の
    ///   <c>assert_notice_in_sync</c>、マージの前は <c>validate.yml</c> が見る。
    ///   <b>このワークフローに Unity ジョブは無い</b>ので、ここが唯一だとマージまで誰も気づけない。
    ///   ここが受け持つのは、Editor から読む経路（設定パネルの「このアプリについて」）の分。
    /// </summary>
    [TestFixture]
    public sealed class NoticeTests
    {
        [Test]
        public void StreamingAssetsCopyMatchesTheRepositoryNotice()
        {
            // Assets/ → chatter-mascot/ → リポジトリのルート
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            var source = Path.Combine(root, "NOTICE");
            var copy = Path.Combine(Application.dataPath, "StreamingAssets", "NOTICE.txt");

            Assert.That(File.Exists(source), Is.True, $"リポジトリの NOTICE が見つかりません: {source}");
            Assert.That(
                File.Exists(copy), Is.True,
                $"同梱の NOTICE.txt がありません: {copy}。cp NOTICE {copy} で作ること");

            Assert.That(
                File.ReadAllText(copy), Is.EqualTo(File.ReadAllText(source)),
                "同梱の NOTICE.txt がリポジトリの NOTICE と違います。" +
                "cp NOTICE chatter-mascot/Assets/StreamingAssets/NOTICE.txt で合わせること");
        }
    }
}
