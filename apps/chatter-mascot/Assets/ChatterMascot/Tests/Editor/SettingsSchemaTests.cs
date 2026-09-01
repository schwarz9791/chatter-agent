using System.Collections.Generic;
using System.Linq;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SettingsSchemaTests
    {
        private static SettingsContext Context(bool reachable = true)
        {
            return new SettingsContext
            {
                CoreReachable = reachable,
                Speakers = new[]
                {
                    new SettingChoice("888753760", "Anneli（ノーマル）"),
                    new SettingChoice("1", "テスト（ノーマル）"),
                },
                SpeakerId = "888753760",
                SpeedScale = 1f,
                Version = "1.2.3",
                LicenseText = "MIT",
            };
        }

        private static SettingSpec Find(IReadOnlyList<SettingSpec> items, string key)
        {
            return items.FirstOrDefault(s => s.Key == key);
        }

        /// <summary>★ 重複すると、どちらの項目を触っても同じ振り分け先に飛ぶ</summary>
        [Test]
        public void KeysAreUnique()
        {
            var keys = SettingsSchema.Build(Context())
                .Where(s => s.Kind != SettingKind.Section)
                .Select(s => s.Key)
                .ToList();

            Assert.That(keys, Is.Unique);
            Assert.That(keys, Is.All.Not.Null.And.All.Not.Empty);
        }

        /// <summary>見出し以外はキーを持ち、見出しはキーを持たない</summary>
        [Test]
        public void SectionsHaveNoKeyAndEverythingElseDoes()
        {
            foreach (var spec in SettingsSchema.Build(Context()))
            {
                if (spec.Kind == SettingKind.Section) Assert.That(spec.Key, Is.Null, spec.Label);
                else Assert.That(spec.Key, Is.Not.Null.And.Not.Empty, spec.Label);
                Assert.That(spec.Label, Is.Not.Null.And.Not.Empty);
            }
        }

        /// <summary>★ 刻みが範囲を割り切らないと、最大値にハンドルが止まらない</summary>
        [Test]
        public void SliderStepsDivideTheirRange()
        {
            foreach (var spec in SettingsSchema.Build(Context()).Where(s => s.Kind == SettingKind.Slider))
            {
                Assert.That(spec.Step, Is.GreaterThan(0f), spec.Key);
                var steps = (spec.Max - spec.Min) / spec.Step;
                Assert.That(
                    steps, Is.EqualTo(System.Math.Round(steps)).Within(0.0001f),
                    $"{spec.Key}: 刻み {spec.Step} が範囲 {spec.Min}〜{spec.Max} を割り切らない");
            }
        }

        [Test]
        public void SliderValuesStartInsideTheirRange()
        {
            foreach (var spec in SettingsSchema.Build(Context()).Where(s => s.Kind == SettingKind.Slider))
            {
                var value = SettingsMapping.Parse(spec.Value, float.NaN);
                Assert.That(value, Is.InRange(spec.Min, spec.Max), spec.Key);
            }
        }

        /// <summary>★ 選択肢に無い値を選択済みにすると、開いた瞬間に別の話者へ切り替わって見える</summary>
        [Test]
        public void ChoiceValuesExistInTheirChoices()
        {
            foreach (var spec in SettingsSchema.Build(Context()).Where(s => s.Kind == SettingKind.Choice))
            {
                if (spec.Choices.Count == 0) continue;
                Assert.That(spec.Choices.Select(c => c.Value), Contains.Item(spec.Value), spec.Key);
            }
        }

        /// <summary>
        /// ★★ サーバーに繋がらなくても<b>項目を消さない</b>。消すと「設定が無い」に見える。
        /// </summary>
        [Test]
        public void KeepsCoreItemsWhenTheServerIsUnreachable()
        {
            var items = SettingsSchema.Build(Context(reachable: false));

            foreach (var key in new[] { SettingKeys.Speaker, SettingKeys.Speed, SettingKeys.SummaryEnabled })
            {
                var spec = Find(items, key);
                Assert.That(spec, Is.Not.Null, $"{key} が消えている");
                Assert.That(spec.Enabled, Is.False, key);
                Assert.That(spec.Note, Is.Not.Empty, $"{key}: 無効な理由が出ていない");
            }
        }

        /// <summary>★ 選択肢が空でも項目は出す（note で理由を出す）</summary>
        [Test]
        public void KeepsTheSpeakerItemWithNoChoices()
        {
            var context = Context();
            context.Speakers = new SettingChoice[0];

            var spec = Find(SettingsSchema.Build(context), SettingKeys.Speaker);

            Assert.That(spec, Is.Not.Null);
            Assert.That(spec.Choices, Is.Empty);
            Assert.That(spec.Enabled, Is.False);
            Assert.That(spec.Note, Is.Not.Empty);
        }

        /// <summary>
        /// ★ 環境変数が勝っているキーは触らせない。触れると <c>PATCH</c> が 409 を返すだけで、
        ///   ユーザーには「変えたのに戻る」としか見えない。
        /// </summary>
        [Test]
        public void DisablesKeysThatTheEnvironmentOverrides()
        {
            var context = Context();
            context.CoreEnvOverridden = new[] { CoreConfigKeys.SpeakerId };

            var spec = Find(SettingsSchema.Build(context), SettingKeys.Speaker);

            Assert.That(spec.Enabled, Is.False);
            Assert.That(spec.Note, Does.Contain("CHATTER_AGENT_TTS_SPEAKER_ID"));
        }

        /// <summary>
        /// ★★ 実装が無い項目を出さないこと。押しても何も起きない項目は
        ///   「動いて見える死体」で、グレーアウトでも「今はできない」と「壊れている」を
        ///   ユーザーが区別できない。
        /// </summary>
        [Test]
        public void DoesNotOfferFeaturesThatDoNotExistYet()
        {
            var keys = SettingsSchema.Build(Context()).Select(s => s.Key).ToList();

            // #83（音声出力デバイス）と #70（発話・感情モーション）
            Assert.That(keys, Has.None.EqualTo("outputDevice"));
            Assert.That(keys, Has.None.EqualTo("speechMotion"));
            Assert.That(keys, Has.None.EqualTo("coolMotion"));
            Assert.That(keys, Has.None.EqualTo("cuteMotion"));
        }

        /// <summary>★ 画面に出るのは記号（⌃⌥M）。保存される文字列（ctrl+opt+m）ではない</summary>
        [Test]
        public void ShowsHotKeysAsSymbols()
        {
            var context = Context();
            context.Settings = MascotSettings.Defaults.WithMuteHotKey("ctrl+opt+m");

            var spec = Find(SettingsSchema.Build(context), SettingKeys.MuteHotKey);

            Assert.That(spec.Value, Is.EqualTo("⌃⌥M"));
        }

        /// <summary>★ 壊れた値でも空にしない（画面から気づけるように）</summary>
        [Test]
        public void ShowsUnreadableHotKeysAsIs()
        {
            var context = Context();
            context.Settings = MascotSettings.Defaults.WithMuteHotKey("これは壊れている");

            Assert.That(Find(SettingsSchema.Build(context), SettingKeys.MuteHotKey).Value,
                Is.EqualTo("これは壊れている"));
        }

        [Test]
        public void ShowsTheVersionAndLicense()
        {
            var items = SettingsSchema.Build(Context());

            Assert.That(Find(items, SettingKeys.Version).Value, Does.Contain("1.2.3"));
            Assert.That(Find(items, SettingKeys.License).Value, Is.EqualTo("MIT"));
        }

        [Test]
        public void ReflectsTheCurrentSettings()
        {
            var context = Context();
            context.Settings = MascotSettings.Defaults
                .WithVolume(0.3f)
                .WithBlink(false)
                .WithVrmFileName("foo.vrm");

            var items = SettingsSchema.Build(context);

            Assert.That(Find(items, SettingKeys.Volume).Value, Is.EqualTo("0.3"));
            Assert.That(Find(items, SettingKeys.Blink).Value, Is.EqualTo("false"));
            Assert.That(Find(items, SettingKeys.Vrm).Note, Does.Contain("foo.vrm"));
        }

        [Test]
        public void SaysWhichModelIsUsedWhenNoneIsChosen()
        {
            Assert.That(Find(SettingsSchema.Build(Context()), SettingKeys.Vrm).Note, Is.Not.Empty);
        }

        /// <summary>★ null を渡されても落ちないこと（起動直後に呼ばれうる）</summary>
        [Test]
        public void SurvivesANullContext()
        {
            Assert.That(SettingsSchema.Build(null), Is.Not.Empty);
        }
    }
}
