using ChatterMascot.Net;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>/v1/*</c> のエラーを日本語にする写像（<c>CoreConfigClient.DescribeError</c>）。
    ///
    /// ★★ <b>知らないキーは生のまま出る。</b> それ自体は正しい（嘘の説明より読める）が、
    ///   <b>core がエラーを増やしたのに写像を足し忘れる</b>と、パネルに
    ///   <c>synthesis_unavailable</c> のような英語のキーがそのまま出る。
    ///   実際 #76 のレビュー B-5 まで、テスト音声の失敗がその状態だった。
    ///
    /// ★ <b>「エンジンに繋がりません」と「合成できませんでした」を混ぜないこと。</b>
    ///   前者は話者一覧（<c>GET /v1/speakers</c>）、後者はテスト音声
    ///   （<c>POST /v1/tts/preview</c>）で、切り分けの手掛かりが別。
    /// </summary>
    [TestFixture]
    public sealed class CoreConfigClientErrorTests
    {
        [Test]
        public void NamesTheReasonWhenSynthesisFails()
        {
            Assert.That(
                CoreConfigClient.DescribeError("{\"error\":\"synthesis_unavailable\",\"detail\":\"down\"}"),
                Is.EqualTo("音声を合成できませんでした"));
        }

        /// <summary>★ 「繋がらない」ではない。利用者自身が切っている（→ core の `ttsPreview`）</summary>
        [Test]
        public void SaysWhoTurnedTheAudioOff()
        {
            Assert.That(
                CoreConfigClient.DescribeError("{\"error\":\"tts_disabled\"}"),
                Is.EqualTo("サーバー側で音声が無効になっています（ttsEnabled）"));
        }

        /// <summary>
        /// ★★ <b>404 は「そのサーバーに制御 API が無い」。</b> <c>/v1</c> を持たない
        ///   <c>chatter-agent-server</c> は <c>not found</c> という<b>プレーンテキスト2語</b>を
        ///   返すので、本文をそのまま出すと利用者に届くのはそれだけになる ——
        ///   音声スタイル・話す速さ・要約がまとめて無効になっている理由に辿り着けない。
        ///
        /// ★ 英語のまま出すのは、これが設定の説明ではなく<b>配線の診断</b>だから。
        /// ★★ <b>「版が違う」と言い切らないこと。</b> 404 が言っているのは口が無いことだけで、
        ///   なぜ無いのか（古い / 別物 / 将来消した）はこちらの推論にすぎない。
        /// </summary>
        [Test]
        public void NamesTheMissingApiInsteadOfEchoingNotFound()
        {
            var reason = CoreConfigClient.DescribeFailure(404, "not found\n");

            Assert.That(reason, Does.Contain("API not found"));
            Assert.That(reason, Does.Contain("chatter-agent-server"));
            Assert.That(reason, Does.Not.Contain("not found\n"), "★ 本文を素通しさせない");
        }

        /// <summary>★ 404 以外は今までどおり本文の <c>error</c> を訳す</summary>
        [Test]
        public void StillTranslatesTheBodyForOtherStatuses()
        {
            Assert.That(
                CoreConfigClient.DescribeFailure(403, "{\"error\":\"readonly_key\",\"key\":\"ttsBaseUrl\"}"),
                Is.EqualTo("この設定は変更できません（ttsBaseUrl）"));
        }

        /// <summary>★ 本文が無いときはステータスだけでも出す（「失敗しました」で終わらせない）</summary>
        [Test]
        public void FallsBackToTheStatusWhenThereIsNoBody()
        {
            Assert.That(CoreConfigClient.DescribeFailure(500, ""), Does.Contain("500"));
        }

        /// <summary>★ 知らないキーは生のまま。空にすると「押したのに何も起きない」に見える</summary>
        [Test]
        public void FallsBackToTheRawKey()
        {
            Assert.That(CoreConfigClient.DescribeError("{\"error\":\"future_error\"}"), Is.EqualTo("future_error"));
        }
    }
}
