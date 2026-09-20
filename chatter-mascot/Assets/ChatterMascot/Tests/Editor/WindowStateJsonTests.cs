using ChatterMascot.Window;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class WindowStateJsonTests
    {
        private static readonly PointRect Rect = new PointRect(1770f, 1598f, 300f, 480f);
        private const string Signature = "0,0 3840x2130;1041,-1111 1800x1072";

        [Test]
        public void RoundTrips()
        {
            var json = WindowStateJson.Write(new WindowState(Rect, Signature));

            Assert.That(WindowStateJson.TryParse(json, out var state, out var error), Is.True, error);
            Assert.That(state.Rect, Is.EqualTo(Rect));
            Assert.That(state.DisplaySignature, Is.EqualTo(Signature));
        }

        /// <summary>単位を書いておく。人が開いたときに px と読み違えないため。</summary>
        [Test]
        public void WritesTheUnitAndVersion()
        {
            var json = WindowStateJson.Write(new WindowState(Rect, Signature));

            Assert.That(json, Does.Contain("\"unit\""));
            Assert.That(json, Does.Contain("points"));
            Assert.That(json, Does.Contain("\"version\""));
        }

        [Test]
        public void RejectsAnUnknownVersion()
        {
            Assert.That(WindowStateJson.TryParse(
                "{\"version\":99,\"rect\":{\"x\":0,\"y\":0,\"width\":300,\"height\":480}}",
                out _, out var error), Is.False);
            Assert.That(error, Does.Contain("version"));
        }

        [Test]
        public void RejectsBrokenJson()
        {
            Assert.That(WindowStateJson.TryParse("{ぐちゃぐちゃ", out _, out var error), Is.False);
            Assert.That(error, Is.Not.Null);
        }

        [Test]
        public void RejectsEmptyInput()
        {
            Assert.That(WindowStateJson.TryParse("", out _, out _), Is.False);
            Assert.That(WindowStateJson.TryParse(null, out _, out _), Is.False);
            Assert.That(WindowStateJson.TryParse("   ", out _, out _), Is.False);
        }

        [Test]
        public void RejectsANonObjectRoot()
        {
            Assert.That(WindowStateJson.TryParse("[1,2,3]", out _, out _), Is.False);
        }

        [Test]
        public void RejectsAMissingRect()
        {
            Assert.That(WindowStateJson.TryParse("{\"version\":1}", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("rect"));
        }

        /// <summary>★ 保存ファイルは人が編集しうる。読んだ値をそのまま信じない。</summary>
        [Test]
        public void RejectsARectThatIsNotARectangle()
        {
            Assert.That(WindowStateJson.TryParse(
                "{\"version\":1,\"rect\":{\"x\":0,\"y\":0,\"width\":0,\"height\":480}}",
                out _, out var error), Is.False);
            Assert.That(error, Does.Contain("不正"));
        }

        [Test]
        public void RejectsNonNumericFields()
        {
            Assert.That(WindowStateJson.TryParse(
                "{\"version\":1,\"rect\":{\"x\":\"0\",\"y\":0,\"width\":300,\"height\":480}}",
                out _, out _), Is.False);
        }

        /// <summary>手で整数に書き直されても読めること。</summary>
        [Test]
        public void AcceptsIntegersAsWellAsFloats()
        {
            Assert.That(WindowStateJson.TryParse(
                "{\"version\":1,\"rect\":{\"x\":10,\"y\":20,\"width\":300,\"height\":480}}",
                out var state, out var error), Is.True, error);
            Assert.That(state.Rect.X, Is.EqualTo(10f));
        }

        /// <summary>displays が無くても読める（指紋が空＝「構成が違う」扱いになるだけ）。</summary>
        [Test]
        public void AcceptsAMissingDisplaySignature()
        {
            Assert.That(WindowStateJson.TryParse(
                "{\"version\":1,\"rect\":{\"x\":10,\"y\":20,\"width\":300,\"height\":480}}",
                out var state, out _), Is.True);
            Assert.That(state.DisplaySignature, Is.EqualTo(string.Empty));
        }
    }
}
