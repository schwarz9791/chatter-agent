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
            var written = SettingsJson.Write(MascotSettings.Defaults.WithMuted(true).WithMuteHotKey("cmd+shift+m").WithHideHotKey("cmd+shift+h"));
            var parsed = Parse(written);

            Assert.That(parsed.Muted, Is.True);
            Assert.That(parsed.MuteHotKey, Is.EqualTo("cmd+shift+m"));
            Assert.That(parsed.HideHotKey, Is.EqualTo("cmd+shift+h"));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void UsesDefaultsForAnEmptyObject()
        {
            var parsed = Parse("{}");
            Assert.That(parsed.Muted, Is.EqualTo(MascotSettings.Defaults.Muted));
            Assert.That(parsed.MuteHotKey, Is.EqualTo(HotKeySpec.Default));
            Assert.That(parsed.HideHotKey, Is.EqualTo(HotKeySpec.DefaultHide));
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

        /// <summary>ui は audio と同じ作法（キー単位で既定に倒す / 未知キーは無視）。</summary>
        [Test]
        public void ReadsTheHideHotKey()
        {
            var parsed = Parse("{\"ui\":{\"hideHotKey\":\"cmd+shift+h\"}}");

            Assert.That(parsed.HideHotKey, Is.EqualTo("cmd+shift+h"));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void RejectsAnUnregisterableHideHotKey()
        {
            var parsed = Parse("{\"ui\":{\"hideHotKey\":\"h\"}}");

            Assert.That(parsed.HideHotKey, Is.EqualTo(HotKeySpec.DefaultHide));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void IgnoresUnknownKeysUnderUi()
        {
            Parse("{\"ui\":{\"nope\":1}}");
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void WarnsWhenUiIsNotAnObject()
        {
            Parse("{\"ui\": 1}");
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

        // ── #76 で増えた項目 ─────────────────────────────────

        /// <summary>★ キーが増えても <c>version</c> を上げない（既存の設定が1回リセットされる）</summary>
        [Test]
        public void KeepsTheFormatVersionWhenKeysAreAdded()
        {
            Assert.That(SettingsJson.CurrentVersion, Is.EqualTo(1));
        }

        [Test]
        public void RoundTripsEveryValue()
        {
            var source = MascotSettings.Defaults
                .WithMuted(true)
                .WithVolume(0.3f)
                .WithIdleMotion(false)
                .WithCursorGaze(false)
                .WithBlink(false)
                .WithVrmFileName("foo.vrm");

            MascotSettings parsed;
            string error;
            Assert.That(SettingsJson.TryParse(SettingsJson.Write(source), out parsed, out error, null), Is.True, error);
            Assert.That(parsed, Is.EqualTo(source));
        }

        /// <summary>★ スライダー由来の 0.7000000119 をそのまま残さない</summary>
        [Test]
        public void RoundsSliderNoiseBeforeWriting()
        {
            var written = SettingsJson.Write(MascotSettings.Defaults.WithVolume(0.7000000119f));

            Assert.That(written, Does.Contain("0.7"));
            Assert.That(written, Does.Not.Contain("0.70000"));
        }

        /// <summary>
        /// ★ 範囲外を「不正」として既定に倒さないこと。範囲を狭めたときに、
        ///   前の版で保存された値が全部既定へ飛ぶ。
        /// </summary>
        [Test]
        public void ClampsOutOfRangeNumbersInsteadOfResettingThem()
        {
            MascotSettings parsed;
            string error;
            var raw = "{\"version\":1,\"audio\":{\"volume\":9.0}}";

            Assert.That(SettingsJson.TryParse(raw, out parsed, out error, null), Is.True, error);
            Assert.That(parsed.Volume, Is.EqualTo(SettingsMapping.VolumeMax));
        }

        /// <summary>
        /// ★★ キャラクターの大きさは <c>window.json</c> が持つ。
        ///   ここに書くと権威が2つになるので、**書かないし読まない**
        ///   （前の版が書いた <c>character.scale</c> は未知キーとして警告して無視する）。
        /// </summary>
        [Test]
        public void DoesNotStoreTheCharacterSize()
        {
            Assert.That(SettingsJson.Write(MascotSettings.Defaults), Does.Not.Contain("scale"));

            MascotSettings parsed;
            string error;
            var warnings = new List<string>();
            Assert.That(
                SettingsJson.TryParse(
                    "{\"version\":1,\"character\":{\"scale\":1.4,\"blink\":false}}",
                    out parsed, out error, warnings.Add),
                Is.True, error);

            Assert.That(parsed.Blink, Is.False, "他のキーは読めること");
            Assert.That(warnings, Has.Some.Contains("scale"));
        }

        /// <summary>★ 数値ですらないときは既定に倒す（クランプする先が無い）</summary>
        [Test]
        public void FallsBackWhenANumberIsNotANumber()
        {
            MascotSettings parsed;
            string error;
            var warnings = new List<string>();

            Assert.That(
                SettingsJson.TryParse(
                    "{\"version\":1,\"audio\":{\"volume\":\"おおきく\"}}",
                    out parsed, out error, warnings.Add),
                Is.True, error);
            Assert.That(parsed.Volume, Is.EqualTo(MascotSettings.Defaults.Volume));
            Assert.That(warnings, Is.Not.Empty);
        }

        /// <summary>
        /// ★★ VRM の名前に区切り文字を通さないこと。この値は
        ///   <c>models/</c> に連結されるので、<c>../</c> でランタイムルートの外を指せる。
        /// </summary>
        [Test]
        public void RejectsVrmNamesWithPathSeparators()
        {
            MascotSettings parsed;
            string error;
            var warnings = new List<string>();

            Assert.That(
                SettingsJson.TryParse(
                    "{\"version\":1,\"character\":{\"vrm\":\"../../secret.vrm\"}}",
                    out parsed, out error, warnings.Add),
                Is.True, error);
            Assert.That(parsed.VrmFileName, Is.Empty);
            Assert.That(warnings, Is.Not.Empty);
        }

        [Test]
        public void AcceptsAPlainVrmFileName()
        {
            MascotSettings parsed;
            string error;

            Assert.That(
                SettingsJson.TryParse("{\"version\":1,\"character\":{\"vrm\":\" foo.vrm \"}}",
                    out parsed, out error, null),
                Is.True, error);
            Assert.That(parsed.VrmFileName, Is.EqualTo("foo.vrm"));
        }

        /// <summary>★ 新しい版が書いた設定を古い版が読むことは普通に起きる</summary>
        [Test]
        public void IgnoresUnknownKeysInTheNewSections()
        {
            MascotSettings parsed;
            string error;
            var warnings = new List<string>();

            Assert.That(
                SettingsJson.TryParse(
                    "{\"version\":1,\"character\":{\"blink\":false,\"future\":1}}",
                    out parsed, out error, warnings.Add),
                Is.True, error);
            Assert.That(parsed.Blink, Is.False);
            Assert.That(warnings, Is.Not.Empty);
        }

        /// <summary>#75 の頃に書かれた設定（新しいキーが無い）も読めること</summary>
        [Test]
        public void ReadsFilesWrittenBeforeTheNewKeysExisted()
        {
            MascotSettings parsed;
            string error;

            Assert.That(
                SettingsJson.TryParse(
                    "{\"version\":1,\"audio\":{\"mute\":true,\"muteHotKey\":\"ctrl+opt+m\"}," +
                    "\"ui\":{\"hideHotKey\":\"ctrl+opt+h\"}}",
                    out parsed, out error, null),
                Is.True, error);
            Assert.That(parsed.Muted, Is.True);
            Assert.That(parsed.Volume, Is.EqualTo(1f), "新しいキーは既定のまま");
            Assert.That(parsed.IdleMotion, Is.True);
        }

        // ── #88 で増えた display.frameRate ───────────────────

        [Test]
        public void RoundTripsTheFrameRate()
        {
            var written = SettingsJson.Write(MascotSettings.Defaults.WithFrameRate(60));
            var parsed = Parse(written);

            Assert.That(parsed.FrameRate, Is.EqualTo(60));
            Assert.That(_warnings, Is.Empty);
        }

        /// <summary>★ 30/60 の2値しか無いので、範囲外は「近い方」ではなく既定へ倒す</summary>
        [Test]
        public void FallsBackToTheDefaultFrameRateForAnUnknownValue()
        {
            var parsed = Parse("{\"display\":{\"frameRate\":45}}");

            Assert.That(parsed.FrameRate, Is.EqualTo(SettingsMapping.DefaultFrameRate));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void ReadsTheDefaultFrameRateWhenDisplayIsMissing()
        {
            var parsed = Parse("{\"audio\":{\"mute\":true}}");

            Assert.That(parsed.FrameRate, Is.EqualTo(SettingsMapping.DefaultFrameRate));
            Assert.That(_warnings, Is.Empty);
        }

        /// <summary>型が違う値は、そのキーだけ既定に倒す（他のキーと同じ作法）</summary>
        [Test]
        public void FallsBackToTheDefaultFrameRateWhenTheValueIsAString()
        {
            var parsed = Parse("{\"display\":{\"frameRate\":\"60\"}}");

            Assert.That(parsed.FrameRate, Is.EqualTo(SettingsMapping.DefaultFrameRate));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void WarnsWhenDisplayIsNotAnObject()
        {
            var parsed = Parse("{\"display\": 1}");

            Assert.That(parsed.FrameRate, Is.EqualTo(SettingsMapping.DefaultFrameRate));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void IgnoresUnknownKeysUnderDisplay()
        {
            Parse("{\"display\":{\"nope\":1}}");
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>★ 書式のバージョンを見るまでもなく、既定値がそのまま読める形で出ていること</summary>
        [Test]
        public void WritesTheDisplaySection()
        {
            var written = SettingsJson.Write(MascotSettings.Defaults);

            Assert.That(written, Does.Contain("\"display\""));
            Assert.That(written, Does.Contain("\"frameRate\": 30"));
        }
    }
}
