using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ChatterMascot.Net;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using ChatterMascot.Vrm;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 画面の文言の表（<see cref="UiText"/>）。
    ///
    /// ★ 英語の並びに日本語が残っていないこと（訳し漏れ）と、言語で並びが変わらないことを固定する。
    /// </summary>
    [TestFixture]
    public sealed class UiTextTests
    {
        private static readonly Regex Japanese =
            new Regex(@"[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}　-〿＀-￯]");

        private static readonly string[] CoreKeys =
        {
            CoreConfigKeys.SpeakerId, CoreConfigKeys.KokoroVoiceId, CoreConfigKeys.SpeedScale,
            CoreConfigKeys.SummaryEnabled, CoreConfigKeys.AiSummaryBackend, CoreConfigKeys.EmotionClassifier,
        };

        private static readonly string[] ErrorCodes =
        {
            "env_override", "readonly_key", "invalid_value", "unknown_key", "engine_unreachable",
            "synthesis_unavailable", "tts_disabled", "config_unreadable", "config_unwritable",
            "too_many_requests", "future_error",
        };

        [TestCase(SystemLanguage.Japanese, true)]
        [TestCase(SystemLanguage.English, false)]
        [TestCase(SystemLanguage.French, false)]
        [TestCase(SystemLanguage.Unknown, false)]
        public void ForPicksJapaneseOnlyForJapanese(SystemLanguage language, bool japanese)
        {
            Assert.That(UiText.For(language), Is.SameAs(japanese ? UiText.Ja : UiText.En));
        }

        private static IEnumerable<IReadOnlyList<SettingSpec>> Layouts(UiText text)
        {
            var speakers = new[] { new SettingChoice("1", "Anneli (Normal)") };
            var clip = new[] { new SettingChoice("idle/a.vrma", "idle/a.vrma") };
            var steps = SettingsMapping.XrHeightSteps(160f);

            SettingsContext Desktop(Action<SettingsContext> tweak = null)
            {
                var c = new SettingsContext
                {
                    Text = text,
                    CoreReachable = true,
                    Speakers = speakers,
                    SpeakerId = "1",
                    Version = "1.0",
                    LicenseText = "MIT",
                };
                tweak?.Invoke(c);
                return c;
            }

            SettingsContext Xr(Action<SettingsContext> tweak = null)
            {
                var c = new SettingsContext
                {
                    Text = text,
                    Platform = SettingsPlatform.Xr,
                    XrHeightChoices = SettingsMapping.XrHeightChoices(steps, text),
                    MotionClips = clip,
                };
                tweak?.Invoke(c);
                return c;
            }

            var contexts = new List<SettingsContext>
            {
                Desktop(),
                Desktop(c => { c.CoreReachable = false; c.Speakers = new SettingChoice[0]; }),
                Desktop(c => c.Speakers = new SettingChoice[0]),
                Desktop(c => c.CoreEnvOverridden = CoreKeys),
                Desktop(c => c.Settings = c.Settings.WithIdleMotion(false)),
                Desktop(c => c.Settings = c.Settings.WithVrmFileName("a.vrm")),
                Desktop(c => c.Settings = c.Settings.WithMuteHotKey(c.Settings.HideHotKey)),
                Desktop(c => c.MotionClips = null),
                Desktop(c => c.MotionClips = new SettingChoice[0]),
                Desktop(c => c.MotionClips = clip),
                Xr(),
                Xr(c => c.XrHeightChoices = null),
                Xr(c => c.AssetSyncRunning = true),
                Xr(c => c.Settings = c.Settings.WithAssetSync(SettingsMapping.AssetSyncOff)),
                Xr(c => c.MotionClips = null),
                Xr(c => c.MotionClips = new SettingChoice[0]),
                Xr(c => c.Settings = c.Settings.WithIdleMotion(false)),
            };

            foreach (var c in contexts) yield return SettingsSchema.Build(c);
            yield return SettingsSchema.BuildAbout(contexts[0]);
        }

        private static IEnumerable<string> Texts(IEnumerable<SettingSpec> items)
        {
            foreach (var s in items)
            {
                yield return s.Label;
                yield return s.Note;
                if (s.Choices == null) continue;
                foreach (var ch in s.Choices) yield return ch.Label;
            }
        }

        private static void AssertNoJapanese(IEnumerable<string> texts)
        {
            foreach (var t in texts)
            {
                if (string.IsNullOrEmpty(t)) continue;
                Assert.That(Japanese.IsMatch(t), Is.False, t);
            }
        }

        [Test]
        public void EnglishSettingsHaveNoJapanese()
        {
            foreach (var layout in Layouts(UiText.En)) AssertNoJapanese(Texts(layout));
        }

        [Test]
        public void LayoutDoesNotDependOnTheLanguage()
        {
            var ja = Layouts(UiText.Ja).ToList();
            var en = Layouts(UiText.En).ToList();

            Assert.That(en.Count, Is.EqualTo(ja.Count));
            for (var i = 0; i < ja.Count; i++)
            {
                Assert.That(en[i].Select(s => s.Key), Is.EqualTo(ja[i].Select(s => s.Key)), "keys #" + i);
                Assert.That(en[i].Select(s => s.Kind), Is.EqualTo(ja[i].Select(s => s.Kind)), "kinds #" + i);
                Assert.That(en[i].Select(s => s.Enabled), Is.EqualTo(ja[i].Select(s => s.Enabled)), "enabled #" + i);
                // XR のパネルは注記の有無で行を作り直すので、有無も言語で変わらないこと
                Assert.That(
                    en[i].Select(s => string.IsNullOrEmpty(s.Note)),
                    Is.EqualTo(ja[i].Select(s => string.IsNullOrEmpty(s.Note))), "note #" + i);
            }
        }

        /// <summary>
        /// Bridge からしか出ない文言も含め、<see cref="UiText"/> の string を返す全メンバーを検査する。
        /// メンバーを足せば自動で対象に入る。
        /// </summary>
        [Test]
        public void EveryMemberIsTranslated()
        {
            var flags = System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly;
            var members = new List<(string Name, System.Func<UiText, string> Get)>();

            foreach (var p in typeof(UiText).GetProperties(flags))
            {
                if (p.PropertyType != typeof(string) || p.GetMethod == null) continue;
                var prop = p;
                members.Add((prop.Name, t => (string)prop.GetValue(t)));
            }

            foreach (var m in typeof(UiText).GetMethods(flags))
            {
                if (m.IsSpecialName || m.ReturnType != typeof(string)) continue;
                var method = m;
                var args = method.GetParameters().Select(p =>
                {
                    if (p.ParameterType == typeof(string)) return (object)"x";
                    if (p.ParameterType == typeof(int)) return 2;
                    if (p.ParameterType == typeof(long)) return 500L;
                    Assert.Fail($"{method.Name}: 未対応の引数の型 {p.ParameterType}");
                    return null;
                }).ToArray();
                members.Add((method.Name, t => (string)method.Invoke(t, args)));
            }

            Assert.That(members, Is.Not.Empty, "リフレクションで UiText のメンバーを取れていない");

            foreach (var member in members)
            {
                var en = member.Get(UiText.En);
                Assert.That(en, Is.Not.Null.And.Not.Empty, "En." + member.Name);
                AssertNoJapanese(new[] { en });
                Assert.That(member.Get(UiText.Ja), Is.Not.Null.And.Not.Empty, "Ja." + member.Name);
            }
        }

        [Test]
        public void EnglishMenuHasNoJapanese()
        {
            foreach (var hidden in new[] { false, true })
            {
                var model = MascotMenu.Build(new MenuState(
                    false, hidden, Spec("ctrl+opt+m"), Spec("ctrl+opt+h"),
                    "Chatter Mascot", "1.0", 1, null, null, UiText.En));

                AssertNoJapanese(model.Entries.Where(e => !e.IsSeparator).Select(e => e.Label));
                AssertNoJapanese(new[] { model.Tooltip });
            }
        }

        [Test]
        public void EnglishPanelJsonHasNoJapanese()
        {
            var json = JObject.Parse(SettingsPanelJson.Write("x", new List<SettingSpec>(), UiText.En));

            AssertNoJapanese(json["strings"].Children<JProperty>().Select(p => (string)p.Value));
        }

        [Test]
        public void EnglishXrHeightChoicesHaveNoJapanese()
        {
            var choices = SettingsMapping.XrHeightChoices(SettingsMapping.XrHeightSteps(160f), UiText.En);

            AssertNoJapanese(choices.Select(c => c.Label));
            Assert.That(choices.Last().Label, Is.EqualTo("Life-size (160 cm)"));
        }

        [Test]
        public void EnglishMotionPlayNoticesHaveNoJapanese()
        {
            foreach (MotionPlayResult result in Enum.GetValues(typeof(MotionPlayResult)))
            {
                var notice = SettingsSchema.MotionPlayNotice(result, "happy/a.vrma", UiText.En);
                AssertNoJapanese(new[] { notice });
                Assert.That(notice, Is.Not.Empty, result.ToString());
            }
        }

        [Test]
        public void EnglishServerErrorsHaveNoJapanese()
        {
            foreach (var code in ErrorCodes)
            {
                var body = "{\"error\":\"" + code + "\",\"key\":\"ttsSpeakerId\"}";
                var ja = CoreConfigClient.DescribeError(body, UiText.Ja);
                var en = CoreConfigClient.DescribeError(body, UiText.En);

                Assert.That(en, Is.Not.Empty, code);
                AssertNoJapanese(new[] { en });
                if (code != "future_error") Assert.That(en, Is.Not.EqualTo(ja), code);
            }
            AssertNoJapanese(new[] { CoreConfigClient.DescribeFailure(500, "", UiText.En) });
        }

        [Test]
        public void EnglishHotKeyErrorsHaveNoJapanese()
        {
            HotKeySpec spec;
            string error;

            Assert.That(HotKeySpec.TryFromCode(0x2E, 0, UiText.En, out spec, out error), Is.False);
            AssertNoJapanese(new[] { error });

            Assert.That(HotKeySpec.TryFromCode(0x21, 0x1000, UiText.En, out spec, out error), Is.False);
            AssertNoJapanese(new[] { error });
        }

        [Test]
        public void EnglishMenuShortcutUsesHalfWidthParentheses()
        {
            var model = MascotMenu.Build(new MenuState(
                false, false, Spec("ctrl+opt+m"), default(HotKeySpec),
                "Chatter Mascot", "1.0", 1, null, null, UiText.En));

            Assert.That(model.Entries.First(e => e.Key == MenuKeys.Mute).Label, Is.EqualTo("Mute (⌃⌥M)"));
            Assert.That(model.Entries.First(e => e.Key == MenuKeys.About).Label, Is.EqualTo("About Chatter Mascot"));
        }

        [Test]
        public void EnglishShortcutClashNameTheOtherRow()
        {
            var settings = MascotSettings.Defaults.WithMuteHotKey(MascotSettings.Defaults.HideHotKey);
            var items = SettingsSchema.Build(new SettingsContext { Text = UiText.En, Settings = settings });

            var note = items.First(s => s.Key == SettingKeys.HideHotKey).Note;
            Assert.That(note, Is.EqualTo("Same as \"Toggle mute\", so it can't be registered."));
        }

        private static HotKeySpec Spec(string text)
        {
            HotKeySpec spec;
            string error;
            HotKeySpec.TryParse(text, out spec, out error);
            return spec;
        }
    }
}
