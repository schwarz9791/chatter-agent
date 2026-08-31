using System.Collections.Generic;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SettingsJsonTests
    {
        private List<string> _warnings;

        [SetUp]
        public void SetUp()
        {
            _warnings = new List<string>();
        }

        private MascotSettings Parse(string raw)
        {
            MascotSettings settings;
            string error;
            Assert.That(SettingsJson.TryParse(raw, out settings, out error, _warnings.Add), Is.True, error);
            return settings;
        }

        private string Reject(string raw)
        {
            MascotSettings settings;
            string error;
            Assert.That(SettingsJson.TryParse(raw, out settings, out error, _warnings.Add), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            return error;
        }

        [Test]
        public void RoundTrips()
        {
            var written = SettingsJson.Write(new MascotSettings(true, "cmd+shift+m"));
            var parsed = Parse(written);

            Assert.That(parsed.Muted, Is.True);
            Assert.That(parsed.MuteHotKey, Is.EqualTo("cmd+shift+m"));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void UsesDefaultsForAnEmptyObject()
        {
            var parsed = Parse("{}");
            Assert.That(parsed.Muted, Is.EqualTo(MascotSettings.Defaults.Muted));
            Assert.That(parsed.MuteHotKey, Is.EqualTo(HotKeySpec.Default));
        }

        /// <summary>★ ファイル全体が読めないケース。呼び出し側は直前値を維持する。</summary>
        [Test]
        public void RejectsBrokenJson()
        {
            Reject("{ぐちゃぐちゃ");
            Reject("");
            Reject("[1, 2, 3]");
            Reject("\"文字列\"");
        }

        [Test]
        public void RejectsAnUnknownVersion()
        {
            Reject("{\"version\": 999}");
        }

        /// <summary>知らないキーは警告して無視する。★ 既定に戻したり throw したりしない。</summary>
        [Test]
        public void IgnoresUnknownKeysWithAWarning()
        {
            var parsed = Parse("{\"audio\":{\"mute\":true,\"nope\":1},\"future\":42}");

            Assert.That(parsed.Muted, Is.True, "知っているキーは生かす");
            Assert.That(_warnings, Has.Count.EqualTo(2));
        }

        /// <summary>型が違う値は、そのキーだけ既定に倒す（他のキーは生きる）。</summary>
        [Test]
        public void FallsBackPerKeyOnBadValues()
        {
            var parsed = Parse("{\"audio\":{\"mute\":\"yes\",\"muteHotKey\":\"cmd+shift+m\"}}");

            Assert.That(parsed.Muted, Is.False, "mute だけ既定へ");
            Assert.That(parsed.MuteHotKey, Is.EqualTo("cmd+shift+m"), "他のキーは生きる");
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>
        /// ★★ <b>登録できないショートカットを保存させないこと。</b> 修飾キー無しを通すと、
        /// 次の起動でそのキーが全アプリから奪われる（→ <c>HotKeySpec</c>）。
        /// </summary>
        [Test]
        public void RejectsAnUnregisterableHotKey()
        {
            var parsed = Parse("{\"audio\":{\"muteHotKey\":\"m\"}}");

            Assert.That(parsed.MuteHotKey, Is.EqualTo(HotKeySpec.Default));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void WarnsWhenAudioIsNotAnObject()
        {
            var parsed = Parse("{\"audio\": 1}");

            Assert.That(parsed.Muted, Is.False);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>version が無いファイルも読む（手で書いたものを弾かない）。</summary>
        [Test]
        public void AcceptsAFileWithoutAVersion()
        {
            Assert.That(Parse("{\"audio\":{\"mute\":true}}").Muted, Is.True);
        }
    }
}
