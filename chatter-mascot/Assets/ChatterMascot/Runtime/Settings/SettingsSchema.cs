using System;
using System.Collections.Generic;
using ChatterMascot.Ui;
using ChatterMascot.Vrm;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// 設定項目のキー。XR の設定パネルが項目を取り違えないための語彙。
    /// </summary>
    public static class SettingKeys
    {
        /// <summary>XR の設定パネルだけに出す</summary>
        public const string Mute = "mute";

        /// <summary>XR の「大きさ」（cm）</summary>
        public const string XrHeight = "xrHeight";

        /// <summary>
        /// モデルとモーションをサーバーから受け取るか（→ <see cref="MascotSettings.AssetSync"/>）。
        /// XR の設定パネルだけに出す。
        /// </summary>
        public const string AssetSync = "assetSync";

        /// <summary>今すぐ同期する。XR の設定パネルだけに出す。押した瞬間だけ意味を持つ（値は持たない）。</summary>
        public const string AssetSyncNow = "assetSyncNow";

        /// <summary>
        /// 「モーションを確認」の選択中の1本。<b>保存しない</b>
        /// （→ <see cref="SettingsContext.MotionPreview"/> の doc）。
        /// </summary>
        public const string MotionPreview = "motionPreview";

        /// <summary>「モーションを確認」の「再生」ボタン。押した瞬間だけ意味を持つ</summary>
        public const string MotionPreviewPlay = "motionPreviewPlay";

        public const string CursorGaze = "cursorGaze";
        public const string Blink = "blink";

        /// <summary>XR で歩行範囲の円を出し、指した先へ歩かせるか。XR の設定パネルだけに出す</summary>
        public const string Walk = "walk";

        /// <summary>ペアリングのサブページを開く／閉じる。XR の設定パネルだけに出す</summary>
        public const string PairingOpen = "pairingOpen";
        public const string PairingBack = "pairingBack";

        /// <summary>PIN の桁の Choice。<c>PairingDigit0</c>〜<c>PairingDigit3</c></summary>
        public const string PairingDigit0 = "pairingDigit0";
        public const string PairingDigit1 = "pairingDigit1";
        public const string PairingDigit2 = "pairingDigit2";
        public const string PairingDigit3 = "pairingDigit3";

        /// <summary>端末のキーボードで PIN を入れる。入力が済むと、そのままペアリングへ進む</summary>
        public const string PairingKeyboard = "pairingKeyboard";

        /// <summary>PIN を送って繋ぎ直す。押した瞬間だけ意味を持つ</summary>
        public const string PairingClaim = "pairingClaim";

        public const string ResetPosition = "resetPosition";
        public const string ResetAll = "resetAll";
    }

    /// <summary>
    /// XR の設定パネルの<b>並び順を持つ唯一の場所</b>。
    ///
    /// ★★ <b>「押しても何も起きない項目」を出さないこと。</b> グレーアウトで出す案も採らない ——
    ///   「今はできない」と「壊れている」がユーザーから区別できない。実装が無いものは
    ///   <b>項目ごと出さず、ここにコメントで場所だけ確保する</b>。
    /// </summary>
    public static class SettingsSchema
    {
        /// <summary>
        /// XR の設定パネル（<c>Xr/XrSettingsPanel</c>）向けの並び。
        ///
        /// ★ <b>ここに出さないもの</b>: 待機モーションの ON/OFF・フレームレート・
        ///   音声系（ミュートを除く）・AI要約・感情判定・終了。手のひらメニュー／歯車から開く前提で、
        ///   項目を絞る。
        /// ★ <b>ミュートだけは出す。</b> XR にはパネル以外に切り替える手段が無い。
        /// </summary>
        public static IReadOnlyList<SettingSpec> BuildXr(SettingsContext context)
        {
            var c = context ?? new SettingsContext();
            if (c.PairingOpen) return BuildXrPairing(c);

            var settings = c.Settings;
            var text = c.Text;
            var items = new List<SettingSpec>();

            items.Add(SettingSpec.Bool(SettingKeys.Mute, text.Mute, settings.Muted));

            // ── キャラクター ─────────────────────────────────
            items.Add(SettingSpec.Section(text.SectionCharacter));
            items.Add(BuildXrHeightChoice(c, settings));
            var syncOn = settings.AssetSync != SettingsMapping.AssetSyncOff;
            items.Add(SettingSpec.Bool(
                SettingKeys.AssetSync, text.XrSyncAssets,
                syncOn,
                note: text.AppliesFromNextLaunch));
            // ★ note はどの状態でも出す。有無が変わると XrSettingsPanel.Signature が変わってパネルが
            //   行を作り直し、縮尺と中心がずれて、押そうとした行がずれる。
            items.Add(SettingSpec.Button(
                SettingKeys.AssetSyncNow, text.XrSyncNow,
                enabled: syncOn && !c.AssetSyncRunning,
                note: c.AssetSyncRunning ? text.XrSyncing : !syncOn ? text.XrSyncOffNote : text.XrSyncNowNote));

            // ── モーション ───────────────────────────────────
            items.Add(SettingSpec.Section(text.SectionMotion));
            AddMotionPreview(items, c, settings);
            items.Add(SettingSpec.Bool(SettingKeys.Walk, text.XrWalk, settings.Walk));
            items.Add(SettingSpec.Bool(SettingKeys.CursorGaze, text.XrCursorGaze, settings.CursorGaze));
            items.Add(SettingSpec.Bool(SettingKeys.Blink, text.Blink, settings.Blink));

            // ── 接続 ─────────────────────────────────────────
            // ★ 入力の行はここに直に足さない。XrSettingsPanel は高さの上限で全体を縮めるので、
            //   行が増えると文字が小さくなる——入力はサブページに逃がす
            items.Add(SettingSpec.Section(text.SectionPairing));
            items.Add(SettingSpec.Button(SettingKeys.PairingOpen, text.XrPairOpen));

            // ── リセット ─────────────────────────────────────
            items.Add(SettingSpec.Section(text.SectionReset));
            items.Add(SettingSpec.Button(SettingKeys.ResetPosition, text.XrResetPosition));
            // ★ モデルファイルは消さない。
            //   同期して取ってきたものを次の起動でまた取りに行けばよいので、消す必要が無い
            items.Add(SettingSpec.Button(
                SettingKeys.ResetAll, text.XrResetAll, note: text.XrResetAllNote));

            return items;
        }

        private static readonly SettingChoice[] PinDigits = BuildPinDigits();

        private static SettingChoice[] BuildPinDigits()
        {
            var digits = new SettingChoice[10];
            for (var i = 0; i < digits.Length; i++)
            {
                var d = i.ToString();
                digits[i] = new SettingChoice(d, d);
            }
            return digits;
        }

        private static readonly string[] PairingDigitKeys =
        {
            SettingKeys.PairingDigit0, SettingKeys.PairingDigit1, SettingKeys.PairingDigit2, SettingKeys.PairingDigit3,
        };

        /// <summary>
        /// <see cref="PairingDigitKeys"/> の桁（0〜3）。桁のキーでなければ -1。
        /// </summary>
        public static int PairingDigitIndex(string key)
        {
            return Array.IndexOf(PairingDigitKeys, key);
        }

        /// <summary>
        /// ペアリングのサブページ。<see cref="SettingsContext.PairingOpen"/> のときの並び。
        ///
        /// ★ <b>ボタンの note は常に出す。</b> 有無が変わると <c>XrSettingsPanel.Signature</c> が変わって
        ///   行を作り直し、押そうとした行がずれる（→ <see cref="BuildXr"/> の同期ボタン）。
        ///   結果の通知は呼び出し側が note を上書きする。
        /// </summary>
        private static IReadOnlyList<SettingSpec> BuildXrPairing(SettingsContext c)
        {
            var text = c.Text;
            var items = new List<SettingSpec>();

            items.Add(SettingSpec.Section(text.SectionPairing));
            for (var i = 0; i < PairingDigitKeys.Length; i++)
            {
                items.Add(SettingSpec.Choice(PairingDigitKeys[i], text.XrPairingDigit(i), PinDigitAt(c.PairingPin, i), PinDigits));
            }
            items.Add(SettingSpec.Button(SettingKeys.PairingKeyboard, text.XrPairKeyboard, enabled: !c.PairingRunning));
            items.Add(SettingSpec.Button(
                SettingKeys.PairingClaim, text.XrPair,
                enabled: !c.PairingRunning,
                note: c.PairingRunning ? text.XrPairing : text.XrPairNote));
            items.Add(SettingSpec.Button(SettingKeys.PairingBack, text.XrBack));
            return items;
        }

        /// <summary>
        /// キーボードの入力から PIN を取り出す。数字（半角）以外は捨て、ちょうど 4 桁でなければ <c>null</c>。
        /// </summary>
        public static string ParseTypedPin(string typed)
        {
            if (typed == null) return null;
            var digits = new System.Text.StringBuilder(4);
            foreach (var ch in typed)
            {
                if (ch >= '0' && ch <= '9') digits.Append(ch);
            }
            return digits.Length == 4 ? digits.ToString() : null;
        }

        /// <summary>
        /// <paramref name="pin"/> の <paramref name="index"/> 桁目。数字でなければ "0"。
        /// ★ 選択中の値が選択肢に無い状態を作らないための保険（<c>ChoiceValuesExistInTheirChoices</c>）。
        /// </summary>
        public static string PinDigitAt(string pin, int index)
        {
            if (pin == null || index >= pin.Length) return "0";
            var ch = pin[index];
            return ch >= '0' && ch <= '9' ? ch.ToString() : "0";
        }

        /// <summary>
        /// 「大きさ」の行。<see cref="SettingsContext.XrHeightChoices"/> が <c>null</c>
        /// （モデルの実寸がまだ分からない）間は選べない——段を実寸から作る
        /// <see cref="SettingsMapping.XrHeightSteps"/> はモデルを読み込んでからでないと呼べない。
        /// </summary>
        private static SettingSpec BuildXrHeightChoice(SettingsContext c, MascotSettings settings)
        {
            var choices = c.XrHeightChoices;
            if (choices == null)
            {
                return SettingSpec.Choice(
                    SettingKeys.XrHeight, c.Text.Size, "", null,
                    enabled: false, note: c.Text.XrLoadingModel);
            }

            return SettingSpec.Choice(
                SettingKeys.XrHeight, c.Text.Size, SnappedXrHeightValue(choices, settings.XrHeight), choices);
        }

        /// <summary>
        /// 現在の cm（<see cref="MascotSettings.XrHeight"/>）に一番近い選択肢の <c>Value</c>。
        ///
        /// ★ <see cref="SettingsMapping.NearestXrHeight"/> が返す <c>float</c> をそのまま
        ///   フォーマットし直すのではなく、<paramref name="choices"/> 側の文字列を返すこと——
        ///   <c>ChoiceValuesExistInTheirChoices</c> が守る不変条件（選択中の値は選択肢の中に
        ///   あること）を、書式の丸め誤差に左右されずに満たすため。
        /// </summary>
        private static string SnappedXrHeightValue(IReadOnlyList<SettingChoice> choices, float currentCm)
        {
            if (choices.Count == 0) return "";

            var steps = new float[choices.Count];
            for (var i = 0; i < steps.Length; i++) steps[i] = SettingsMapping.Parse(choices[i].Value, 0f);

            var nearest = SettingsMapping.NearestXrHeight(currentCm, steps);
            for (var i = 0; i < steps.Length; i++)
            {
                if (steps[i] == nearest) return choices[i].Value;
            }
            return choices[0].Value;
        }

        /// <summary>
        /// 「モーションを確認」の Choice + 「再生」Button（→ <see cref="BuildXr"/>）。
        ///
        /// ★★ 保存しない。settings.json にも core にも書かない
        ///   （→ SettingsContext.MotionPreview の doc）——本番の発話に連動する再生と
        ///   混同しないため、確認用の選択はパネルを閉じたら忘れてよい。
        ///
        /// ★ ラベルは出さない。見出し「モーション」の直下に置くので、
        ///   何の行かは見出しで分かる。
        /// </summary>
        private static void AddMotionPreview(List<SettingSpec> items, SettingsContext c, MascotSettings settings)
        {
            // ★ #70 派生。ファイル名と実際に再生されるモーションが一致しているかを、
            //   本番と同じ経路（VrmMotionPlayer.Play）で確かめられるようにする。
            var motionClips = c.MotionClips;
            var motionEnabled = settings.IdleMotion && motionClips != null && motionClips.Count > 0;
            var motionValue = EffectiveMotionPreview(motionClips, c.MotionPreview);
            items.Add(SettingSpec.Choice(
                SettingKeys.MotionPreview, "", motionValue, motionClips,
                enabled: motionEnabled, note: MotionPreviewNote(motionClips, settings.IdleMotion, c.Text)));
            items.Add(SettingSpec.Button(SettingKeys.MotionPreviewPlay, c.Text.Play, enabled: motionEnabled));
        }

        /// <summary>
        /// <see cref="MotionClip"/> 1本の選択肢 id / ラベル。<b>"&lt;カテゴリ&gt;/&lt;ファイル名&gt;"</b>
        /// （例: <c>"idle/Hub_Idle01.vrma"</c>）。
        ///
        /// ★ <b>id とラベルを同じ文字列にすること。</b> この項目の目的は「ファイル名と
        ///   モーションが一致しているか」を確かめることなので、見せる文言を作り込むと
        ///   かえって確かめにくくなる。
        /// ★ <b>id → <c>MotionClip</c> の引き当ても<see cref="MotionPreviewId"/>を使うこと。</b>
        ///   変換を2箇所に書くと、片方だけ直したときに一致しなくなる。
        /// </summary>
        public static string MotionPreviewId(MotionClip clip)
        {
            return MotionCategories.DirectoryName(clip.Category) + "/" + clip.FileName;
        }

        /// <summary>
        /// #70 派生。<see cref="MotionClip"/> 一覧 → 選択肢一覧への<b>純粋</b>変換。
        ///
        /// ★★ <b><c>null</c> を <c>null</c> のまま返すこと。</b> 空配列にすると
        ///   「読み込み中」（まだ一覧が無い）と「読み込み済みだが1本も無い」が
        ///   区別できなくなる（→ <see cref="SettingsContext.MotionClips"/> の doc）。
        /// ★ <c>MotionClip</c> は Runtime の型（<c>Assets/ChatterMascot/Runtime/Vrm/AnimationManifest.cs</c>）
        ///   なのでここから使ってよいが、<c>ChatterMascot.Vrm</c> アセンブリ（VRM10 依存）の型
        ///   （<c>VrmCharacter</c> など）は持ち込まないこと——テストが1行も当たらなくなる。
        /// </summary>
        public static IReadOnlyList<SettingChoice> MotionPreviewChoices(IReadOnlyList<MotionClip> clips)
        {
            if (clips == null) return null;

            var choices = new SettingChoice[clips.Count];
            for (var i = 0; i < clips.Count; i++)
            {
                var id = MotionPreviewId(clips[i]);
                choices[i] = new SettingChoice(id, id);
            }
            return choices;
        }

        /// <summary>
        /// 「モーションを確認」の無効化の理由。優先順は
        /// <b>待機モーション OFF → 読み込み中 → 一覧が空</b>。
        ///
        /// ★ OFF を先に見ること。OFF にした本人にとっては「読み込み中」や「一覧が空」より
        ///   こちらが直接の理由——<c>VrmMotionPlayer.Play</c> は待機モーション OFF の間
        ///   常に拒否するので、一覧の状態に関わらず押しても何も起きない。
        /// </summary>
        private static string MotionPreviewNote(IReadOnlyList<SettingChoice> clips, bool idleMotionEnabled, UiText text)
        {
            if (!idleMotionEnabled) return text.MotionPreviewIdleOff;
            if (clips == null) return text.MotionPreviewLoading;
            if (clips.Count == 0) return text.MotionPreviewEmpty;
            return "";
        }

        /// <summary>
        /// 「モーションを確認」で<b>実際に</b>選ばれている id。<see cref="SettingsContext.MotionPreview"/> が
        /// 空（一度も触っていない）か一覧に無ければ先頭、一覧が無ければ空文字。
        ///
        /// ★★ <b>表示（<see cref="BuildXr"/>）と「再生」の解決の
        ///   両方がここを通ること。</b> 表示だけ先頭に倒して、押した側が生の
        ///   <c>MotionPreview</c>（空）を引くと、<b>一度も選び直していないときに「再生」が
        ///   「選べるモーションがありません」になる</b>（実機で踏んだ。2026-09-05）。
        /// </summary>
        public static string EffectiveMotionPreview(IReadOnlyList<SettingChoice> choices, string selected)
        {
            if (choices == null || choices.Count == 0) return "";
            return ContainsChoiceValue(choices, selected) ? selected : choices[0].Value;
        }

        /// <summary>
        /// ★ <see cref="SettingsContext.MotionPreview"/> が古い一覧の id を持ち越していても
        ///   壊れないための保険。一覧が変わった直後（マニフェストの読み込み完了・
        ///   ファイルの追加）に、存在しない id を選択済みのまま出すと
        ///   <c>SettingsSchemaTests.ChoiceValuesExistInTheirChoices</c> が固定した不変条件
        ///   （選択中の値は選択肢の中にあること）が破れる。
        /// </summary>
        private static bool ContainsChoiceValue(IReadOnlyList<SettingChoice> choices, string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (var i = 0; i < choices.Count; i++)
            {
                if (string.Equals(choices[i].Value, value, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 「モーションを確認」の「再生」ボタンの結果通知（#70 レビュー #5）。
        /// <c>VrmMotionPlayer.Play</c> の拒否条件と 1 対 1 で対応する<b>純粋関数</b>——
        /// 呼び手はこれを呼ぶだけにして、文言をここの
        /// テストで固定する。
        /// </summary>
        /// <param name="result">再生を試みた結果</param>
        /// <param name="id">試みたモーションの id（<see cref="MotionPreviewId"/> の形。<see cref="MotionPlayResult.Started"/> の文言にだけ使う）</param>
        public static string MotionPlayNotice(MotionPlayResult result, string id, UiText text)
        {
            switch (result)
            {
                case MotionPlayResult.Started: return text.MotionPlayStarted(id);
                case MotionPlayResult.Busy: return text.MotionPlayBusy;
                case MotionPlayResult.IdleNotLoaded: return text.MotionPlayIdleNotLoaded;
                case MotionPlayResult.IdleDisabled: return text.MotionPlayIdleDisabled;
                case MotionPlayResult.NotLoaded: return text.MotionPlayNotLoaded;
                case MotionPlayResult.Disposed: return text.MotionPlayDisposed;
                default: return "";
            }
        }
    }
}
