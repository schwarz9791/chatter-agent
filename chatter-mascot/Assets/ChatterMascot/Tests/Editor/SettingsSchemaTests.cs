using System;
using System.Collections.Generic;
using System.Linq;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SettingsSchemaTests
    {
        private static SettingSpec Find(IReadOnlyList<SettingSpec> items, string key)
        {
            return items.FirstOrDefault(s => s.Key == key);
        }

        /// <summary>★ 重複すると、どちらの項目を触っても同じ振り分け先に飛ぶ</summary>
        [Test]
        public void KeysAreUnique()
        {
            var keys = SettingsSchema.BuildXr(XrContext())
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
            foreach (var spec in SettingsSchema.BuildXr(XrContext()))
            {
                if (spec.Kind == SettingKind.Section) Assert.That(spec.Key, Is.Null, spec.Label);
                else Assert.That(spec.Key, Is.Not.Null.And.Not.Empty, spec.Label);
                // ★ 「モーションを確認」は見出しの直下に置くのでラベルを持たない
                if (spec.Key != SettingKeys.MotionPreview) Assert.That(spec.Label, Is.Not.Null.And.Not.Empty);
            }
        }

        /// <summary>★ 選択肢に無い値を選択済みにすると、開いた瞬間に別の選択肢へ切り替わって見える</summary>
        [Test]
        public void ChoiceValuesExistInTheirChoices()
        {
            foreach (var spec in SettingsSchema.BuildXr(XrContext()).Where(s => s.Kind == SettingKind.Choice))
            {
                if (spec.Choices.Count == 0) continue;
                Assert.That(spec.Choices.Select(c => c.Value), Contains.Item(spec.Value), spec.Key);
            }
        }

        /// <summary>
        /// ★ 表示と「再生」が同じ解決を通ること。一度も選び直していない（空）なら先頭、
        ///   一覧に無い id なら先頭、一覧が無ければ空文字（実機で「選べるモーションがありません」を踏んだ）。
        /// </summary>
        [Test]
        public void EffectiveMotionPreviewFallsBackToTheFirstChoice()
        {
            var choices = new[] { new SettingChoice("idle/a.vrma", "idle/a.vrma"), new SettingChoice("sad/b.vrma", "sad/b.vrma") };

            Assert.That(SettingsSchema.EffectiveMotionPreview(choices, ""), Is.EqualTo("idle/a.vrma"));
            Assert.That(SettingsSchema.EffectiveMotionPreview(choices, "sad/b.vrma"), Is.EqualTo("sad/b.vrma"));
            Assert.That(SettingsSchema.EffectiveMotionPreview(choices, "gone/x.vrma"), Is.EqualTo("idle/a.vrma"));
            Assert.That(SettingsSchema.EffectiveMotionPreview(null, "sad/b.vrma"), Is.EqualTo(""));
            Assert.That(SettingsSchema.EffectiveMotionPreview(new SettingChoice[0], ""), Is.EqualTo(""));
        }

        /// <summary>
        /// ★★ 実装が無い項目を出さないこと。押しても何も起きない項目は
        ///   「動いて見える死体」で、グレーアウトでも「今はできない」と「壊れている」を
        ///   ユーザーが区別できない。
        /// </summary>
        [Test]
        public void DoesNotOfferFeaturesThatDoNotExistYet()
        {
            var keys = SettingsSchema.BuildXr(XrContext()).Select(s => s.Key).ToList();

            // #83（音声出力デバイス）と #70（発話・感情モーション）
            Assert.That(keys, Has.None.EqualTo("outputDevice"));
            Assert.That(keys, Has.None.EqualTo("speechMotion"));
            Assert.That(keys, Has.None.EqualTo("coolMotion"));
            Assert.That(keys, Has.None.EqualTo("cuteMotion"));
        }

        /// <summary>★ null を渡されても落ちないこと（起動直後に呼ばれうる）</summary>
        [Test]
        public void SurvivesANullContext()
        {
            Assert.That(SettingsSchema.BuildXr(null), Is.Not.Empty);
        }

        // ── #70 派生: モーションを確認 ─────────────────────────────

        private static readonly MotionClip Idle01 =
            new MotionClip(MotionCategory.Idle, "/x/idle/Hub_Idle01.vrma", "Hub_Idle01.vrma", MotionStyle.Natural);

        private static readonly MotionClip Idle02 =
            new MotionClip(MotionCategory.Idle, "/x/idle/Hub_Idle02.vrma", "Hub_Idle02.vrma", MotionStyle.Natural);

        private static readonly MotionClip Wave =
            new MotionClip(MotionCategory.Happy, "/x/happy/Wave.vrma", "Wave.vrma", MotionStyle.Natural);

        /// <summary>
        /// ★ id もラベルも <c>"&lt;カテゴリ&gt;/&lt;ファイル名&gt;"</c>。ファイル名と実際の
        ///   モーションが一致しているかを確かめるのがこの項目の目的なので、見せる文言を
        ///   作り込まない。
        /// ★ 並びはカテゴリ順・ファイル名順（呼び出し側——<c>VrmCharacter.MotionClips</c>——が
        ///   <c>MotionCategories.All</c> の順に連結する。ここではその連結済みの一覧を渡すだけ）。
        /// </summary>
        [Test]
        public void ConvertsMotionClipsToChoicesInCategoryAndFileOrder()
        {
            var choices = SettingsSchema.MotionPreviewChoices(new[] { Idle01, Idle02, Wave });

            Assert.That(choices.Count, Is.EqualTo(3));
            Assert.That(choices[0].Value, Is.EqualTo("idle/Hub_Idle01.vrma"));
            Assert.That(choices[0].Label, Is.EqualTo("idle/Hub_Idle01.vrma"));
            Assert.That(choices[1].Value, Is.EqualTo("idle/Hub_Idle02.vrma"));
            Assert.That(choices[2].Value, Is.EqualTo("happy/Wave.vrma"));
        }

        /// <summary>
        /// ★★ <c>null</c> を <c>null</c> のまま返すこと。「読み込み中」（一覧が無い）と
        ///   「読み込み済みだが1本も無い」を区別するための契約（→ <c>SettingsContext.MotionClips</c>）。
        /// </summary>
        [Test]
        public void MotionPreviewChoicesKeepsTheLoadingStateDistinctFromEmpty()
        {
            Assert.That(SettingsSchema.MotionPreviewChoices(null), Is.Null);
            Assert.That(SettingsSchema.MotionPreviewChoices(Array.Empty<MotionClip>()), Is.Empty);
        }

        [Test]
        public void DisablesMotionPreviewWhileTheManifestIsLoading()
        {
            var context = XrContext();
            context.MotionClips = null; // 読み込み中

            var choice = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreview);
            var button = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreviewPlay);

            Assert.That(choice.Enabled, Is.False);
            Assert.That(choice.Note, Does.Contain("読み込み中"));
            Assert.That(button.Enabled, Is.False);
        }

        [Test]
        public void DisablesMotionPreviewWhenThereAreNoClips()
        {
            var context = XrContext();
            context.MotionClips = Array.Empty<SettingChoice>();

            var choice = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreview);
            var button = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreviewPlay);

            Assert.That(choice.Enabled, Is.False);
            Assert.That(choice.Note, Does.Contain("animations"));
            Assert.That(button.Enabled, Is.False);
        }

        /// <summary>★ VrmMotionPlayer.Play は待機モーション OFF の間は常に拒否するので、押しても何も起きない</summary>
        [Test]
        public void DisablesMotionPreviewWhenIdleMotionIsOff()
        {
            var context = XrContext();
            context.MotionClips = SettingsSchema.MotionPreviewChoices(new[] { Idle01 });
            context.Settings = MascotSettings.Defaults.WithIdleMotion(false);

            var choice = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreview);
            var button = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreviewPlay);

            Assert.That(choice.Enabled, Is.False);
            Assert.That(choice.Note, Does.Contain("待機モーション"));
            Assert.That(button.Enabled, Is.False);
        }

        /// <summary>★ 選択済みの値が無ければ先頭を既定にする（→ ChoiceValuesExistInTheirChoices の不変条件）</summary>
        [Test]
        public void DefaultsMotionPreviewToTheFirstClip()
        {
            var context = XrContext();
            context.MotionClips = SettingsSchema.MotionPreviewChoices(new[] { Idle01, Idle02 });

            var choice = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreview);

            Assert.That(choice.Value, Is.EqualTo("idle/Hub_Idle01.vrma"));
        }

        [Test]
        public void KeepsTheChosenMotionPreview()
        {
            var context = XrContext();
            context.MotionClips = SettingsSchema.MotionPreviewChoices(new[] { Idle01, Idle02 });
            context.MotionPreview = "idle/Hub_Idle02.vrma";

            var choice = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreview);

            Assert.That(choice.Value, Is.EqualTo("idle/Hub_Idle02.vrma"));
        }

        // ── XR の設定パネル ─────────────────────────────

        private static SettingsContext XrContext()
        {
            return new SettingsContext
            {
                XrHeightChoices = SettingsMapping.XrHeightChoices(SettingsMapping.XrHeightSteps(160f), UiText.Ja),
            };
        }

        [Test]
        public void XrOffersOnlyTheListedKeysInOrder()
        {
            var keys = SettingsSchema.BuildXr(XrContext()).Select(s => s.Key).ToList();

            Assert.That(keys, Is.EqualTo(new[]
            {
                SettingKeys.Mute,
                null, SettingKeys.XrHeight, SettingKeys.AssetSync, SettingKeys.AssetSyncNow,
                null, SettingKeys.MotionPreview, SettingKeys.MotionPreviewPlay,
                SettingKeys.Walk, SettingKeys.CursorGaze, SettingKeys.Blink,
                null, SettingKeys.ResetPosition, SettingKeys.ResetAll,
            }));
        }

        [Test]
        public void XrReflectsTheWalkSetting()
        {
            var context = XrContext();
            context.Settings = MascotSettings.Defaults.WithWalk(false);

            var spec = Find(SettingsSchema.BuildXr(context), SettingKeys.Walk);

            Assert.That(spec.Kind, Is.EqualTo(SettingKind.Bool));
            Assert.That(spec.Value, Is.EqualTo("false"));
            Assert.That(spec.Label, Is.EqualTo("歩く"));
        }

        [Test]
        public void XrHeightIsDisabledUntilTheModelIsLoaded()
        {
            var context = XrContext();
            context.XrHeightChoices = null;

            var spec = Find(SettingsSchema.BuildXr(context), SettingKeys.XrHeight);

            Assert.That(spec.Enabled, Is.False);
            Assert.That(spec.Note, Does.Contain("読み込んでいます"));
            Assert.That(spec.Choices, Is.Empty);
        }

        /// <summary>★ 値は現在の cm を段に寄せたもの（→ SettingsMapping.NearestXrHeight）</summary>
        [Test]
        public void XrHeightSnapsTheCurrentValueToTheNearestStep()
        {
            var context = XrContext();
            context.Settings = MascotSettings.Defaults.WithXrHeight(33f); // 15/25/40/60/100/160 の 40 に寄る

            var spec = Find(SettingsSchema.BuildXr(context), SettingKeys.XrHeight);

            Assert.That(spec.Enabled, Is.True);
            Assert.That(spec.Value, Is.EqualTo("40"));
            Assert.That(spec.Choices.Select(c => c.Value), Contains.Item(spec.Value));
        }

        [Test]
        public void XrAssetSyncReflectsWhetherSyncIsOff()
        {
            var on = XrContext();
            on.Settings = MascotSettings.Defaults.WithAssetSync(SettingsMapping.AssetSyncAuto);
            Assert.That(Find(SettingsSchema.BuildXr(on), SettingKeys.AssetSync).Value, Is.EqualTo("true"));

            var off = XrContext();
            off.Settings = MascotSettings.Defaults.WithAssetSync(SettingsMapping.AssetSyncOff);
            Assert.That(Find(SettingsSchema.BuildXr(off), SettingKeys.AssetSync).Value, Is.EqualTo("false"));
        }

        [Test]
        public void XrAssetSyncNoteSaysItAppliesOnTheNextLaunch()
        {
            var spec = Find(SettingsSchema.BuildXr(XrContext()), SettingKeys.AssetSync);
            Assert.That(spec.Note, Does.Contain("次回の起動"));
        }

        [Test]
        public void XrAssetSyncNowIsDisabledWithANoteWhenSyncIsOff()
        {
            var c = XrContext();
            c.Settings = MascotSettings.Defaults.WithAssetSync(SettingsMapping.AssetSyncOff);

            var spec = Find(SettingsSchema.BuildXr(c), SettingKeys.AssetSyncNow);
            Assert.That(spec.Enabled, Is.False);
            Assert.That(spec.Note, Is.Not.Empty);
        }

        [Test]
        public void XrAssetSyncNowIsDisabledWhileSyncing()
        {
            var c = XrContext();
            c.Settings = MascotSettings.Defaults.WithAssetSync(SettingsMapping.AssetSyncAuto);
            c.AssetSyncRunning = true;

            var spec = Find(SettingsSchema.BuildXr(c), SettingKeys.AssetSyncNow);
            Assert.That(spec.Enabled, Is.False);
            Assert.That(spec.Note, Does.Contain("同期しています"));
        }

        [Test]
        public void XrAssetSyncNowIsEnabledWhenSyncIsOnAndIdle()
        {
            var c = XrContext();
            c.Settings = MascotSettings.Defaults.WithAssetSync(SettingsMapping.AssetSyncAuto);

            var spec = Find(SettingsSchema.BuildXr(c), SettingKeys.AssetSyncNow);
            Assert.That(spec.Enabled, Is.True);
            Assert.That(spec.Note, Is.Not.Empty);
        }

        [Test]
        public void XrAssetSyncNowKeepsShowingSyncingWhenSyncIsTurnedOffMidway()
        {
            var c = XrContext();
            c.Settings = MascotSettings.Defaults.WithAssetSync(SettingsMapping.AssetSyncOff);
            c.AssetSyncRunning = true;

            var spec = Find(SettingsSchema.BuildXr(c), SettingKeys.AssetSyncNow);
            Assert.That(spec.Enabled, Is.False);
            Assert.That(spec.Note, Does.Contain("同期しています"));
        }

        /// <summary>★ note の有無が変わるとパネルが作り直されるため、どの状態でも note を出す</summary>
        [Test]
        public void XrAssetSyncNowAlwaysHasANote()
        {
            foreach (var (mode, running) in new[]
            {
                (SettingsMapping.AssetSyncAuto, false),
                (SettingsMapping.AssetSyncAuto, true),
                (SettingsMapping.AssetSyncOff, false),
            })
            {
                var c = XrContext();
                c.Settings = MascotSettings.Defaults.WithAssetSync(mode);
                c.AssetSyncRunning = running;

                var spec = Find(SettingsSchema.BuildXr(c), SettingKeys.AssetSyncNow);
                Assert.That(spec.Note, Is.Not.Empty, $"mode={mode} running={running}");
            }
        }

        /// <summary>★ ラベルは aim レイ向けの言い回しに変わるが、キーは共通</summary>
        [Test]
        public void XrCursorGazeUsesTheAimRayWording()
        {
            var spec = Find(SettingsSchema.BuildXr(XrContext()), SettingKeys.CursorGaze);
            Assert.That(spec.Label, Is.EqualTo("指している先を目で追う"));
        }

        [Test]
        public void XrResetPositionDoesNotMentionTheSize()
        {
            var spec = Find(SettingsSchema.BuildXr(XrContext()), SettingKeys.ResetPosition);
            Assert.That(spec.Label, Is.EqualTo("キャラクターの位置をリセット"));
        }

        /// <summary>★ モデルファイルは消さない。note で接続先が残ることだけ伝える</summary>
        [Test]
        public void XrResetAllKeepsTheConnectionNote()
        {
            var spec = Find(SettingsSchema.BuildXr(XrContext()), SettingKeys.ResetAll);
            Assert.That(spec.Note, Does.Contain("接続先"));
        }

        /// <summary>★ 「モーションを確認」の enabled は一覧の読み込み状態に従う</summary>
        [Test]
        public void DisablesMotionPreviewWhileTheManifestIsLoadingOnXr()
        {
            var context = XrContext();
            context.MotionClips = null; // 読み込み中

            var choice = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreview);
            var button = Find(SettingsSchema.BuildXr(context), SettingKeys.MotionPreviewPlay);

            Assert.That(choice.Enabled, Is.False);
            Assert.That(choice.Note, Does.Contain("読み込み中"));
            Assert.That(button.Enabled, Is.False);
        }

        /// <summary>★ 見出し「モーション」の直下に置くので、ラベルが無くても何の行か分かる</summary>
        [Test]
        public void XrMotionPreviewHasNoLabel()
        {
            var spec = Find(SettingsSchema.BuildXr(XrContext()), SettingKeys.MotionPreview);
            Assert.That(spec.Label, Is.Empty);
        }

        [Test]
        public void XrChoiceValuesExistInTheirChoices()
        {
            var context = XrContext();
            context.Settings = MascotSettings.Defaults.WithXrHeight(999f); // 実寸へクランプされる側

            foreach (var spec in SettingsSchema.BuildXr(context).Where(s => s.Kind == SettingKind.Choice))
            {
                if (spec.Choices.Count == 0) continue;
                Assert.That(spec.Choices.Select(c => c.Value), Contains.Item(spec.Value), spec.Key);
            }
        }

        /// <summary>
        /// <c>MotionPlayResult</c> の6分岐すべてに文言が割り当たっていること
        /// （<c>VrmMotionPlayer.Play</c> の拒否条件と1対1）。
        /// </summary>
        [Test]
        public void MotionPlayNoticeCoversAllSixResults()
        {
            Assert.That(SettingsSchema.MotionPlayNotice(MotionPlayResult.Started, "happy/a.vrma", UiText.Ja),
                Is.EqualTo("happy/a.vrma を再生します"));
            Assert.That(SettingsSchema.MotionPlayNotice(MotionPlayResult.Busy, "happy/a.vrma", UiText.Ja),
                Is.EqualTo("再生中です。終わってからもう一度押してください"));
            Assert.That(SettingsSchema.MotionPlayNotice(MotionPlayResult.IdleNotLoaded, "happy/a.vrma", UiText.Ja),
                Is.EqualTo("待機モーションの VRMA が読めていないので再生できません"));
            Assert.That(SettingsSchema.MotionPlayNotice(MotionPlayResult.IdleDisabled, "happy/a.vrma", UiText.Ja),
                Is.EqualTo("待機モーションが OFF です"));
            Assert.That(SettingsSchema.MotionPlayNotice(MotionPlayResult.NotLoaded, "happy/a.vrma", UiText.Ja),
                Is.EqualTo("このモーションは読み込めていません"));
            Assert.That(SettingsSchema.MotionPlayNotice(MotionPlayResult.Disposed, "happy/a.vrma", UiText.Ja),
                Is.EqualTo("キャラクターが無効です"));
        }
    }
}
