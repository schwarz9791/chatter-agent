using System.Collections.Generic;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using Newtonsoft.Json.Linq;
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
                .WithVrmFileName("foo.vrm")
                .WithWalk(false);

            MascotSettings parsed;
            string error;
            Assert.That(SettingsJson.TryParse(SettingsJson.Write(source), out parsed, out error, null), Is.True, error);
            Assert.That(parsed, Is.EqualTo(source));
        }

        /// <summary>★ character.idleMotion / cursorGaze / blink と同じ流儀で読み書きする</summary>
        [Test]
        public void RoundTripsTheWalkFlag()
        {
            var written = SettingsJson.Write(MascotSettings.Defaults.WithWalk(false));
            var parsed = Parse(written);

            Assert.That(parsed.Walk, Is.False);
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void DefaultsWalkToTrueWhenMissing()
        {
            var parsed = Parse("{\"character\":{\"idleMotion\":true}}");
            Assert.That(parsed.Walk, Is.True);
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
        ///   ここに書くと権威が2つになるので、<c>character</c> には**書かないし読まない**
        ///   （前の版が書いた <c>character.scale</c> は未知キーとして警告して無視する）。
        ///
        /// ★ <b><c>character</c> セクションに絞って見ること。</b> <c>xr.height</c>
        ///   （→ <see cref="MascotSettings.XrHeight"/>）は別概念で、こちらは正当に書く。
        /// </summary>
        [Test]
        public void DoesNotStoreTheCharacterSize()
        {
            var character = (JObject)JObject.Parse(SettingsJson.Write(MascotSettings.Defaults))["character"];
            Assert.That(character.ContainsKey("scale"), Is.False);

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

        // ── connection ───────────────────────────

        [Test]
        public void RoundTripsTheConnectionSection()
        {
            var written = SettingsJson.Write(
                MascotSettings.Defaults.WithServerUrl("ws://192.168.1.5:8570").WithToken("s3cr3t"));
            var parsed = Parse(written);

            Assert.That(parsed.ServerUrl, Is.EqualTo("ws://192.168.1.5:8570"));
            Assert.That(parsed.Token, Is.EqualTo("s3cr3t"));
            Assert.That(_warnings, Is.Empty);
        }

        /// <summary>★ デスクトップの設定パネルは connection を触らないが、保存のたびに落ちないこと</summary>
        [Test]
        public void WritesTheConnectionSectionEvenWhenUnset()
        {
            var written = SettingsJson.Write(MascotSettings.Defaults);

            Assert.That(written, Does.Contain("\"connection\""));
            Assert.That(written, Does.Contain("\"serverUrl\": \"\""));
            Assert.That(written, Does.Contain("\"token\": \"\""));
        }

        [Test]
        public void EmptyServerUrlMeansUnspecified()
        {
            var parsed = Parse("{\"connection\":{\"serverUrl\":\"\"}}");

            Assert.That(parsed.ServerUrl, Is.Empty);
            Assert.That(_warnings, Is.Empty);
        }

        /// <summary>★ ws:// / wss:// 以外は既定（空）へ倒す。設定パネルを壊さないため throw しない。</summary>
        [Test]
        public void FallsBackToTheDefaultForAnInvalidServerUrl()
        {
            var parsed = Parse("{\"connection\":{\"serverUrl\":\"http://example.com\"}}");

            Assert.That(parsed.ServerUrl, Is.Empty);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void AcceptsAWssServerUrl()
        {
            var parsed = Parse("{\"connection\":{\"serverUrl\":\"wss://mascot.example:443\"}}");

            Assert.That(parsed.ServerUrl, Is.EqualTo("wss://mascot.example:443"));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void TrimsTheToken()
        {
            var parsed = Parse("{\"connection\":{\"token\":\" s3cr3t \"}}");

            Assert.That(parsed.Token, Is.EqualTo("s3cr3t"));
        }

        [Test]
        public void AcceptsATokenWithUnderscoresAndDashes()
        {
            var parsed = Parse("{\"connection\":{\"token\":\"a1_B2-c3\"}}");

            Assert.That(parsed.Token, Is.EqualTo("a1_B2-c3"));
            Assert.That(_warnings, Is.Empty);
        }

        /// <summary>
        /// ★ ヘッダに載せられない文字集合はここで弾く（→ サーバーの <c>lanToken.ts</c> の
        ///   <c>TOKEN_PATTERN</c> と同じ）。改行を許すと <c>SetRequestHeader</c> が例外を投げうる。
        /// </summary>
        [Test]
        public void RejectsATokenWithANewlineInTheMiddle()
        {
            var parsed = Parse("{\"connection\":{\"token\":\"abc\\ndef\"}}");

            Assert.That(parsed.Token, Is.Empty);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void RejectsATokenWithInternalWhitespace()
        {
            var parsed = Parse("{\"connection\":{\"token\":\"abc def\"}}");

            Assert.That(parsed.Token, Is.Empty);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>★ <c>openssl rand -base64</c> の出力（<c>+</c> <c>/</c> <c>=</c>）は通さない</summary>
        [Test]
        public void RejectsATokenWithBase64SpecialCharacters()
        {
            var parsed = Parse("{\"connection\":{\"token\":\"abc+def/ghi=\"}}");

            Assert.That(parsed.Token, Is.Empty);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void IgnoresUnknownKeysUnderConnection()
        {
            Parse("{\"connection\":{\"nope\":1}}");
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void WarnsWhenConnectionIsNotAnObject()
        {
            var parsed = Parse("{\"connection\": 1}");

            Assert.That(parsed.ServerUrl, Is.Empty);
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        // ── xr ───────────────────────────

        [Test]
        public void RoundTripsTheXrSection()
        {
            var written = SettingsJson.Write(MascotSettings.Defaults
                .WithXrHeight(60f).WithXrDistance(1.2f).WithXrAzimuth(-90f).WithXrFeetBelowEye(0.8f));
            var parsed = Parse(written);

            Assert.That(parsed.XrHeight, Is.EqualTo(60f));
            Assert.That(parsed.XrDistance, Is.EqualTo(1.2f));
            Assert.That(parsed.XrAzimuth, Is.EqualTo(-90f));
            Assert.That(parsed.XrFeetBelowEye, Is.EqualTo(0.8f));
            Assert.That(_warnings, Is.Empty);
        }

        /// <summary>★ デスクトップの設定パネルは xr を触らないが、保存のたびに落ちないこと</summary>
        [Test]
        public void WritesTheXrSectionEvenWhenUnset()
        {
            var written = SettingsJson.Write(MascotSettings.Defaults);

            Assert.That(written, Does.Contain("\"xr\""));
            Assert.That(written, Does.Contain("\"height\""));
            Assert.That(written, Does.Contain("\"distance\""));
            Assert.That(written, Does.Contain("\"azimuth\""));
            Assert.That(written, Does.Contain("\"feetBelowEye\""));
            // ★ 未換算の倍率が無い（既定）間は scale を書かない——
            //   両方書き続けると、手で height を直しても scale が優先されるように見えかねない
            Assert.That(written, Does.Not.Contain("\"scale\""));
        }

        [Test]
        public void FallsBackToTheDefaultWhenHeightIsAString()
        {
            var parsed = Parse("{\"xr\":{\"height\":\"60\"}}");

            Assert.That(parsed.XrHeight, Is.EqualTo(SettingsMapping.XrDefaultHeight));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void FallsBackToTheDefaultWhenHeightIsNotFinite()
        {
            var parsed = Parse("{\"xr\":{\"height\":NaN}}");

            Assert.That(parsed.XrHeight, Is.EqualTo(SettingsMapping.XrDefaultHeight));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>★ <see cref="SettingsJson"/> の <c>xr</c> はクランプしない。範囲外はそのまま既定へ倒す</summary>
        [Test]
        public void FallsBackToTheDefaultWhenHeightIsOutOfRange()
        {
            var parsed = Parse("{\"xr\":{\"height\":2000}}");

            Assert.That(parsed.XrHeight, Is.EqualTo(SettingsMapping.XrDefaultHeight));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>
        /// ★ <c>xr.height</c> の読み取り範囲は健全性検査だけ（→ <see cref="SettingsMapping.XrHeightReadMin"/> /
        ///   <see cref="SettingsMapping.XrHeightReadMax"/>）。選べる範囲（<see cref="SettingsMapping.XrHeightMin"/>〜
        ///   実寸）の外でも、数値として壊れていなければそのまま読み戻す。
        /// </summary>
        [Test]
        public void ReadsHeightAsIsWithinTheSanityRange()
        {
            Assert.That(Parse("{\"xr\":{\"height\":10}}").XrHeight, Is.EqualTo(10f));
            Assert.That(Parse("{\"xr\":{\"height\":500}}").XrHeight, Is.EqualTo(500f));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void FallsBackToTheDefaultWhenDistanceIsOutOfRange()
        {
            var parsed = Parse("{\"xr\":{\"distance\":10}}");

            Assert.That(parsed.XrDistance, Is.EqualTo(SettingsMapping.XrDefaultDistance));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void FallsBackToTheDefaultWhenAzimuthIsOutOfRange()
        {
            var parsed = Parse("{\"xr\":{\"azimuth\":200}}");

            Assert.That(parsed.XrAzimuth, Is.EqualTo(SettingsMapping.XrDefaultAzimuth));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void FallsBackToTheDefaultWhenFeetBelowEyeIsOutOfRange()
        {
            var parsed = Parse("{\"xr\":{\"feetBelowEye\":5}}");

            Assert.That(parsed.XrFeetBelowEye, Is.EqualTo(SettingsMapping.XrDefaultFeetBelowEye));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void AcceptsTheXrRangeBoundaries()
        {
            var parsed = Parse(
                "{\"xr\":{\"height\":15,\"distance\":5.0,\"azimuth\":-180,\"feetBelowEye\":2.0}}");

            Assert.That(parsed.XrHeight, Is.EqualTo(SettingsMapping.XrHeightMin));
            Assert.That(parsed.XrDistance, Is.EqualTo(SettingsMapping.XrDistanceMax));
            Assert.That(parsed.XrAzimuth, Is.EqualTo(SettingsMapping.XrAzimuthMin));
            Assert.That(parsed.XrFeetBelowEye, Is.EqualTo(SettingsMapping.XrFeetBelowEyeMax));
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void IgnoresUnknownKeysUnderXr()
        {
            Parse("{\"xr\":{\"nope\":1}}");
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void WarnsWhenXrIsNotAnObject()
        {
            var parsed = Parse("{\"xr\": 1}");

            Assert.That(parsed.XrHeight, Is.EqualTo(SettingsMapping.XrDefaultHeight));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        // ── xr.scale（古い書式。未換算の倍率として読む） ─────────

        /// <summary>★ height が無い間、実寸への換算は Runtime の外（XR 側）が行う</summary>
        [Test]
        public void ReadsLegacyScaleAsUnconvertedWhenHeightIsMissing()
        {
            var parsed = Parse("{\"xr\":{\"scale\":0.3}}");

            Assert.That(parsed.XrLegacyScale, Is.EqualTo(0.3f));
            Assert.That(parsed.XrHeight, Is.EqualTo(SettingsMapping.XrDefaultHeight), "換算前は既定のまま");
            Assert.That(_warnings, Is.Empty);
        }

        [Test]
        public void WritesBackTheLegacyScaleUntilItIsResolved()
        {
            var pending = SettingsJson.Write(MascotSettings.Defaults.WithXrLegacyScale(0.3f));
            Assert.That(pending, Does.Contain("\"scale\": 0.3"), "情報を失わないよう書き戻す");

            var resolved = SettingsJson.Write(
                MascotSettings.Defaults.WithXrHeight(60f).WithXrLegacyScale(0f));
            Assert.That(resolved, Does.Not.Contain("\"scale\""), "確定したら書かない");
        }

        [Test]
        public void FallsBackToNoLegacyScaleWhenOutOfRange()
        {
            var parsed = Parse("{\"xr\":{\"scale\":1.5}}");

            Assert.That(parsed.XrLegacyScale, Is.EqualTo(0f));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        [Test]
        public void AcceptsTheLegacyScaleRangeBoundaries()
        {
            var parsed = Parse("{\"xr\":{\"scale\":0.05}}");

            Assert.That(parsed.XrLegacyScale, Is.EqualTo(SettingsMapping.XrScaleMin));
            Assert.That(_warnings, Is.Empty);
        }

        // ── long を超える整数（Newtonsoft は BigInteger で持つ） ───────────────────

        /// <summary>
        /// ★ 型検査（Integer）は通ってしまう値。<c>SpeechFrameParser.TryAsInteger</c> と同じ罠で、
        ///   例外を出さずそのキーだけ既定へ倒すこと。
        /// </summary>
        [Test]
        public void FallsBackPerKeyWhenNumbersExceedLong()
        {
            var parsed = Parse(
                "{\"audio\":{\"volume\":100000000000000000000}," +
                "\"xr\":{\"scale\":100000000000000000000}}");

            Assert.That(parsed.Volume, Is.EqualTo(MascotSettings.Defaults.Volume));
            Assert.That(parsed.XrLegacyScale, Is.EqualTo(0f));
            Assert.That(_warnings, Has.Count.EqualTo(2));
        }

        /// <summary>frameRate は <c>long</c> には収まる値（<c>int</c> 超え）でも既定へ倒す。</summary>
        [Test]
        public void FallsBackToTheDefaultFrameRateWhenTheValueExceedsInt()
        {
            var parsed = Parse("{\"display\":{\"frameRate\":3000000000}}");

            Assert.That(parsed.FrameRate, Is.EqualTo(SettingsMapping.DefaultFrameRate));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>frameRate が <c>long</c> も超える（<c>BigInteger</c>）ときも同じ経路で既定へ倒す。</summary>
        [Test]
        public void FallsBackToTheDefaultFrameRateWhenTheValueExceedsLong()
        {
            var parsed = Parse("{\"display\":{\"frameRate\":100000000000000000000}}");

            Assert.That(parsed.FrameRate, Is.EqualTo(SettingsMapping.DefaultFrameRate));
            Assert.That(_warnings, Has.Count.EqualTo(1));
        }

        /// <summary>version がここで <c>long</c> を超えても、既定へ倒さず読み込み全体を拒否する。</summary>
        [Test]
        public void RejectsAVersionExceedingLong()
        {
            Reject("{\"version\":100000000000000000000}");
        }
    }
}
