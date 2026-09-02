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

        /// <summary>★ 知らないキーは生のまま。空にすると「押したのに何も起きない」に見える</summary>
        [Test]
        public void FallsBackToTheRawKey()
        {
            Assert.That(CoreConfigClient.DescribeError("{\"error\":\"future_error\"}"), Is.EqualTo("future_error"));
        }
    }
}
