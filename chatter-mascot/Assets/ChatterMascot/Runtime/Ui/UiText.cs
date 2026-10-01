using UnityEngine;

namespace ChatterMascot.Ui
{
    /// <summary>
    /// 画面に出す文言の表。<b>ログは持たない。</b>
    ///
    /// ★ 端末の言語は<b>呼び出し側</b>が読んで <see cref="For"/> に渡す。<c>Runtime</c> の純粋関数を
    ///   テストで固定するため、ここでは <c>Application.systemLanguage</c> を読まない。
    /// ★ 言語を足すときは、サブクラスを1つ足して <see cref="For"/> に分岐を足す。
    ///   抽象メンバーを埋め忘れるとコンパイルが通らない。
    /// </summary>
    public abstract class UiText
    {
        public static readonly UiText Ja = new JaUiText();
        public static readonly UiText En = new EnUiText();

        /// <summary>日本語なら <see cref="Ja"/>、それ以外は <see cref="En"/>。</summary>
        public static UiText For(SystemLanguage language)
        {
            return language == SystemLanguage.Japanese ? Ja : En;
        }

        // ── メニュー ──────────────────────────────────
        public abstract string MenuMute { get; }
        public abstract string MenuHide { get; }
        public abstract string MenuShow { get; }
        public abstract string MenuSettings { get; }
        public abstract string MenuQuit { get; }
        /// <summary>メニューの項目と「について」の題名で共有する</summary>
        public abstract string About(string product);
        /// <summary>★ 括弧は言語ごとに違う（全角 / 半角）ので書式ごと持つ</summary>
        public abstract string MenuWithShortcut(string label, string symbols);

        // ── 設定パネル: 共通 ──────────────────────────────────
        public abstract string SettingsTitle(string product);
        public abstract string PanelRecord { get; }
        public abstract string PanelCancel { get; }
        public abstract string PanelRecording { get; }
        public abstract string PanelEmpty { get; }
        public abstract string CoreUnreachable { get; }
        /// <summary>Desktop と XR で共有する</summary>
        public abstract string NoMotionToPlay { get; }

        // ── 設定パネル: キャラクター ──────────────────────────────────
        public abstract string SectionCharacter { get; }
        public abstract string ChooseVrm { get; }
        public abstract string BundledModelNote { get; }
        public abstract string Size { get; }

        // ── 設定パネル: オーディオ ──────────────────────────────────
        public abstract string SectionAudio { get; }
        public abstract string VoiceStyle { get; }
        public abstract string Volume { get; }
        public abstract string SpeakingSpeed { get; }
        public abstract string PlayTestVoice { get; }
        public abstract string SpeakerListUnavailable { get; }
        public abstract string AppliesFromNextSentence { get; }
        /// <summary>★ 「効かない」ではなく「なぜ効かないか」を出す。環境変数名まで出せば「自分で決めた」と分かる</summary>
        public abstract string EnvOverridden(string envName);

        // ── 設定パネル: モーション ──────────────────────────────────
        public abstract string SectionMotion { get; }
        public abstract string IdleMotion { get; }
        public abstract string PreviewMotion { get; }
        public abstract string Play { get; }
        public abstract string MotionPreviewIdleOff { get; }
        public abstract string MotionPreviewLoading { get; }
        public abstract string MotionPreviewEmpty { get; }
        public abstract string CursorGaze { get; }
        public abstract string Blink { get; }
        public abstract string FrameRate { get; }
        /// <summary>★ 倍率を文言に持ち込まない。マシン・MSAA・電源状態で変わる</summary>
        public abstract string FrameRateNote { get; }

        // ── 設定パネル: AI要約 ──────────────────────────────────
        public abstract string SectionAiSummary { get; }
        public abstract string SummarizeLongMessages { get; }
        public abstract string SummarizeNote { get; }
        public abstract string SummaryEngine { get; }
        public abstract string SummaryEngineNote { get; }

        // ── 設定パネル: 感情判定 ──────────────────────────────────
        public abstract string SectionEmotion { get; }
        public abstract string EmotionEngine { get; }
        public abstract string EmotionEngineNote { get; }
        public abstract string EmotionDictionary { get; }

        // ── 設定パネル: ショートカット ──────────────────────────────────
        public abstract string SectionShortcuts { get; }
        public abstract string ShortcutsNote { get; }
        public abstract string HotKeyMute { get; }
        public abstract string HotKeyHide { get; }
        /// <summary>★ 括弧・鉤括弧は書式ごと持つ</summary>
        public abstract string HotKeyClashNote(string label);
        public abstract string HotKeyClashRejected(string label);
        public abstract string HotKeyNeedsModifier { get; }
        public abstract string HotKeyKeyNotAllowed { get; }
        public abstract string HotKeyRecordedEmpty { get; }
        public abstract string HotKeyRecordedUnreadable(string text);

        // ── 設定パネル: リセット ──────────────────────────────────
        public abstract string SectionReset { get; }
        public abstract string ResetPosition { get; }
        public abstract string ResetAll { get; }
        public abstract string ResetAllNote { get; }
        public abstract string Quit { get; }
        public abstract string PositionReset { get; }

        // ── 設定パネル: 注記・通知 ──────────────────────────────────
        public abstract string SpeakerIdUnreadable { get; }
        public abstract string PlaybackNotReady { get; }
        public abstract string MutedNoSound { get; }
        public abstract string AppliesFromNextLaunch { get; }

        // ── 設定パネル: 「について」 ──────────────────────────────────
        public abstract string Version { get; }
        public abstract string License { get; }

        // ── 確認ダイアログ ──────────────────────────────────
        public abstract string ConfirmResetTitle { get; }
        public abstract string ConfirmResetMessage { get; }
        public abstract string ConfirmResetOk { get; }
        public abstract string ConfirmResetCancel { get; }
        /// <summary><paramref name="removedModels"/> が 0 なら件数に触れない</summary>
        public abstract string ResetDone(int removedModels);
        public abstract string ResetModelsFailed(string reason);
        public abstract string ResetCoreFailed(string reason);
        public abstract string ResetCoreDefaultsUnavailable { get; }
        public abstract string ResetKeyFailed(string key, string reason);

        // ── VRM の選択 ──────────────────────────────────
        public abstract string ChooseVrmTitle { get; }
        public abstract string ChooseVrmMessage { get; }
        public abstract string ChooseVrmButton { get; }
        public abstract string NativePluginMissing { get; }
        public abstract string SettingsFolderUnknown { get; }
        public abstract string FileNameUnreadable { get; }
        public abstract string CopyFailed(string reason);

        // ── モーション再生の結果 ──────────────────────────────────
        public abstract string MotionPlayStarted(string id);
        public abstract string MotionPlayBusy { get; }
        public abstract string MotionPlayIdleNotLoaded { get; }
        public abstract string MotionPlayIdleDisabled { get; }
        public abstract string MotionPlayNotLoaded { get; }
        public abstract string MotionPlayDisposed { get; }

        // ── XR ──────────────────────────────────
        public abstract string XrMute { get; }
        public abstract string XrLoadingModel { get; }
        public abstract string XrActualSize(string cm);
        public abstract string XrSyncAssets { get; }
        public abstract string XrSyncAssetsNote { get; }
        public abstract string XrSyncNow { get; }
        public abstract string XrSyncing { get; }
        public abstract string XrSyncOffNote { get; }
        public abstract string XrSyncNowNote { get; }
        public abstract string XrSyncNotStarted { get; }
        public abstract string XrWalk { get; }
        public abstract string XrCursorGaze { get; }
        public abstract string XrResetPosition { get; }
        public abstract string XrResetAll { get; }
        public abstract string XrResetAllNote { get; }
        public abstract string XrResetAllConfirmNote { get; }
        public abstract string XrClose { get; }

        // ── サーバーエラー ──────────────────────────────────
        public abstract string ErrorResponseUnreadable(string reason);
        public abstract string ErrorHttp(long status);
        public abstract string ErrorEnvOverride(string key);
        public abstract string ErrorReadonlyKey(string key);
        public abstract string ErrorInvalidValue(string key);
        public abstract string ErrorUnknownKey(string key);
        public abstract string ErrorEngineUnreachable { get; }
        public abstract string ErrorSynthesisUnavailable { get; }
        public abstract string ErrorTtsDisabled { get; }
        public abstract string ErrorConfigUnreadable { get; }
        public abstract string ErrorConfigUnwritable { get; }
        public abstract string ErrorTooManyRequests { get; }
        /// <summary>サーバーが返した知らない error をそのまま出す（訳せないものを潰さない）。key が空なら error だけ</summary>
        public abstract string ErrorUnknown(string error, string key);
    }
}
