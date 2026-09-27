using ChatterMascot.Net;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>GET /v1/speakers</c> の応答の読み替え（<c>CoreConfigClient.ReadSpeakers</c>）。
    ///
    /// ★ 話者 ID はエンジンによって数値（VOICEVOX）と文字列（OpenAI 互換 TTS）の
    ///   両方があるので、どちらも読めること・空文字は読めない要素として落ちることを見る。
    /// </summary>
    [TestFixture]
    public sealed class CoreConfigClientSpeakersTests
    {
        [Test]
        public void ReadsNumericAndStringIds()
        {
            var body = JToken.Parse(
                "{\"speakers\":[{\"id\":888753760,\"label\":\"Anneli\"},{\"id\":\"af_heart\",\"label\":\"af_heart\"}]}");

            var choices = CoreConfigClient.ReadSpeakers(body);

            Assert.That(choices.Count, Is.EqualTo(2));
            Assert.That(choices[0].Value, Is.EqualTo("888753760"));
            Assert.That(choices[0].Label, Is.EqualTo("Anneli"));
            Assert.That(choices[1].Value, Is.EqualTo("af_heart"));
            Assert.That(choices[1].Label, Is.EqualTo("af_heart"));
        }

        /// <summary>★ 空文字は「読めない要素」として落とす（他は生かす）</summary>
        [Test]
        public void DropsEmptyStringIds()
        {
            var body = JToken.Parse(
                "{\"speakers\":[{\"id\":\"\",\"label\":\"empty\"},{\"id\":\"1\",\"label\":\"ok\"}]}");

            var choices = CoreConfigClient.ReadSpeakers(body);

            Assert.That(choices.Count, Is.EqualTo(1));
            Assert.That(choices[0].Value, Is.EqualTo("1"));
        }
    }
}
