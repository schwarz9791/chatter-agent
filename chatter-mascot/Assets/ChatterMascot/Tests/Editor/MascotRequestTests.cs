using System;
using System.Collections.Generic;
using ChatterMascot.Settings;
using ChatterMascot.Vrm;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class MascotRequestTests
    {
        private static bool Parse(string text, out MascotRequest request, out string reason) =>
            MascotRequest.TryParse(text, out request, out reason);

        [Test]
        public void ParsesResetWindow()
        {
            Assert.That(Parse("{\"version\":1,\"type\":\"resetWindow\"}", out var request, out var reason), Is.True, reason);
            Assert.That(request.Type, Is.EqualTo(MascotRequestType.ResetWindow));
        }

        [Test]
        public void ParsesPlayMotion()
        {
            Assert.That(
                Parse("{\"version\":1,\"type\":\"playMotion\",\"id\":\"idle/Hub_Idle01.vrma\"}", out var request, out var reason),
                Is.True, reason);
            Assert.That(request.Type, Is.EqualTo(MascotRequestType.PlayMotion));
            Assert.That(request.Id, Is.EqualTo("idle/Hub_Idle01.vrma"));
        }

        [TestCase("{\"version\":2,\"type\":\"resetWindow\"}")]
        [TestCase("{\"type\":\"resetWindow\"}")]
        [TestCase("{\"version\":\"1\",\"type\":\"resetWindow\"}")]
        [TestCase("{\"version\":1,\"type\":\"dance\"}")]
        [TestCase("{\"version\":1}")]
        [TestCase("{\"version\":1,\"type\":\"playMotion\"}")]
        [TestCase("{\"version\":1,\"type\":\"playMotion\",\"id\":\"\"}")]
        [TestCase("{\"version\":1,\"type\":\"playMotion\",\"id\":3}")]
        [TestCase("{\"version\":1,\"type\":\"resetWin")]
        [TestCase("[]")]
        [TestCase("")]
        public void RejectsWithAReason(string text)
        {
            Assert.That(Parse(text, out _, out var reason), Is.False);
            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }

        /// <summary>★ 書き手は *.json.tmp に書いてから rename する。書きかけを拾わない。</summary>
        [Test]
        public void OrdersOnlyCompleteJsonNamesByOrdinal()
        {
            var names = MascotRequest.OrderedRequestNames(new[]
            {
                "1700000000002-0001.json",
                "1700000000001-0002.json.tmp",
                "1700000000001-0001.json",
                "note.txt",
                "1700000000002-0000.json",
                "x.JSON",
            });

            Assert.That(names, Is.EqualTo(new[]
            {
                "1700000000001-0001.json",
                "1700000000002-0000.json",
                "1700000000002-0001.json",
            }));
        }

        [Test]
        public void WritesMotionIdsInGivenOrder()
        {
            var clips = new List<MotionClip>
            {
                new MotionClip(MotionCategory.Idle, "/x/idle/Hub_Idle01.vrma", "Hub_Idle01.vrma", MotionStyle.Natural),
                new MotionClip(MotionCategory.Happy, "/x/happy/Wave.vrma", "Wave.vrma", MotionStyle.Natural),
            };

            var root = JObject.Parse(MascotRequest.MotionsJson(clips));

            Assert.That((int)root["version"], Is.EqualTo(1));
            Assert.That(root["motions"].ToObject<string[]>(), Is.EqualTo(new[] { "idle/Hub_Idle01.vrma", "happy/Wave.vrma" }));
        }

        /// <summary>★ 空は「1本も無い」。ファイルが無いこと（読み込み中）とは別の意味。</summary>
        [Test]
        public void WritesAnEmptyArrayForNoMotions()
        {
            var root = JObject.Parse(MascotRequest.MotionsJson(Array.Empty<MotionClip>()));

            Assert.That(root["motions"], Is.InstanceOf<JArray>());
            Assert.That(((JArray)root["motions"]).Count, Is.EqualTo(0));
        }
    }
}
