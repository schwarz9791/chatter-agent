namespace ChatterMascot.Ui
{
    internal sealed class EnUiText : UiText
    {
        // ── メニュー ──────────────────────────────────
        public override string MenuMute => "Mute";
        public override string MenuHide => "Hide Character";
        public override string MenuShow => "Show Character";
        public override string MenuSettings => "Settings…";
        public override string MenuQuit => "Quit";
        public override string About(string product) => $"About {product}";
        public override string MenuWithShortcut(string label, string symbols) => $"{label} ({symbols})";

        // ── 設定パネル: 共通 ──────────────────────────────────
        public override string SettingsTitle(string product) => $"{product} Settings";
        public override string PanelRecord => "Record";
        public override string PanelCancel => "Cancel";
        public override string PanelRecording => "Press keys";
        public override string PanelEmpty => "(Unavailable)";
        public override string CoreUnreachable => "Can't reach the server.";
        public override string NoMotionToPlay => "No motions available.";

        // ── 設定パネル: キャラクター ──────────────────────────────────
        public override string SectionCharacter => "Character";
        public override string ChooseVrm => "Choose VRM Model…";
        public override string BundledModelNote => "Using the bundled model.";
        public override string Size => "Size";

        // ── 設定パネル: オーディオ ──────────────────────────────────
        public override string SectionAudio => "Audio";
        public override string VoiceStyle => "Voice style";
        public override string Volume => "Volume";
        public override string SpeakingSpeed => "Speaking speed";
        public override string PlayTestVoice => "Play Test Voice";
        public override string SpeakerListUnavailable => "Couldn't load the voice list.";
        public override string AppliesFromNextSentence => "Takes effect from the next sentence.";
        public override string EnvOverridden(string envName) => $"Locked by environment variable ({envName}).";

        // ── 設定パネル: モーション ──────────────────────────────────
        public override string SectionMotion => "Motion";
        public override string IdleMotion => "Idle motion";
        public override string PreviewMotion => "Preview motion";
        public override string Play => "Play";
        public override string MotionPreviewIdleOff => "Can't play while idle motion is off.";
        public override string MotionPreviewLoading => "Loading motions…";
        public override string MotionPreviewEmpty => "Put .vrma files in ~/.config/chatter-agent/animations/<emotion>/ to choose them.";
        public override string CursorGaze => "Eyes follow cursor";
        public override string Blink => "Blink";
        public override string FrameRate => "Frame rate";
        public override string FrameRateNote => "60 fps uses more CPU.";

        // ── 設定パネル: AI要約 ──────────────────────────────────
        public override string SectionAiSummary => "AI Summary";
        public override string SummarizeLongMessages => "Summarize long messages before reading aloud";
        public override string SummarizeNote => "Summarizing takes time. If it isn't ready in time, the original is read.";
        public override string SummaryEngine => "Summary engine";
        public override string SummaryEngineNote => "fm requires macOS 27 or later. Otherwise the original is read.";

        // ── 設定パネル: 感情判定 ──────────────────────────────────
        public override string SectionEmotion => "Emotion";
        public override string EmotionEngine => "Emotion engine";
        public override string EmotionEngineNote => "fm requires macOS 27 or later. Otherwise falls back to Dictionary.";
        public override string EmotionDictionary => "Dictionary";

        // ── 設定パネル: ショートカット ──────────────────────────────────
        public override string SectionShortcuts => "Shortcuts";
        public override string ShortcutsNote => "Click Record, then press the keys (at least one modifier).";
        public override string HotKeyMute => "Toggle mute";
        public override string HotKeyHide => "Show/hide character";
        public override string HotKeyClashNote(string label) => $"Same as \"{label}\", so it can't be registered.";
        public override string HotKeyClashRejected(string label) => $"Already used by \"{label}\".";
        public override string HotKeyNeedsModifier => "Press a modifier key (⌃ ⌥ ⇧ ⌘) too.";
        public override string HotKeyKeyNotAllowed => "This key can't be used for shortcuts.";
        public override string HotKeyRecordedEmpty => "The recording is empty.";
        public override string HotKeyRecordedUnreadable(string text) => $"Couldn't read the recording: \"{text}\"";

        // ── 設定パネル: リセット ──────────────────────────────────
        public override string SectionReset => "Reset";
        public override string ResetPosition => "Reset Character Position and Size";
        public override string ResetAll => "Reset All Settings…";
        public override string ResetAllNote => "Also deletes the chosen model file and restores everything to its initial state.";
        public override string Quit => "Quit";
        public override string PositionReset => "Position and size restored to defaults.";

        // ── 設定パネル: 注記・通知 ──────────────────────────────────
        public override string SpeakerIdUnreadable => "Couldn't read the speaker ID.";
        public override string PlaybackNotReady => "Playback isn't ready.";
        public override string MutedNoSound => "Muted, so nothing will play.";
        public override string AppliesFromNextLaunch => "Takes effect on next launch.";

        // ── 設定パネル: 「について」 ──────────────────────────────────
        public override string Version => "Version";
        public override string License => "License";

        // ── 確認ダイアログ ──────────────────────────────────
        public override string ConfirmResetTitle => "Reset all settings?";
        public override string ConfirmResetMessage => "Size, position, volume, motion, shortcuts, voice style, speaking speed, summary, summary engine, and emotion engine settings return to their defaults, and the chosen VRM model file is deleted. This can't be undone.";
        public override string ConfirmResetOk => "Reset";
        public override string ConfirmResetCancel => "Cancel";
        public override string ResetDone(int removedModels) =>
            removedModels > 0 ? $"Restored defaults and deleted {removedModels} model file(s)." : "Restored defaults.";
        public override string ResetModelsFailed(string reason) => $"Couldn't delete the model: {reason}";
        public override string ResetCoreFailed(string reason) => $"Couldn't reset voice style, speaking speed, summary, etc. ({reason})";
        public override string ResetCoreDefaultsUnavailable => "couldn't get the defaults";
        public override string ResetKeyFailed(string key, string reason) => $"Couldn't reset {key} ({reason})";

        // ── VRM の選択 ──────────────────────────────────
        public override string ChooseVrmTitle => "Choose VRM Model";
        public override string ChooseVrmMessage => "The chosen file is copied to models/.";
        public override string ChooseVrmButton => "Choose";
        public override string NativePluginMissing => "Can't choose a file: the native plugin is missing.";
        public override string SettingsFolderUnknown => "Couldn't determine the settings folder.";
        public override string FileNameUnreadable => "Couldn't read the file name.";
        public override string CopyFailed(string reason) => $"Couldn't copy the file: {reason}";

        // ── モーション再生の結果 ──────────────────────────────────
        public override string MotionPlayStarted(string id) => $"Playing {id}.";
        public override string MotionPlayBusy => "Already playing. Try again when it ends.";
        public override string MotionPlayIdleNotLoaded => "Can't play: the idle motion VRMA isn't loaded.";
        public override string MotionPlayIdleDisabled => "Idle motion is off.";
        public override string MotionPlayNotLoaded => "This motion isn't loaded.";
        public override string MotionPlayDisposed => "The character is unavailable.";

        // ── XR ──────────────────────────────────
        public override string XrMute => "Mute";
        public override string XrLoadingModel => "Loading model…";
        public override string XrActualSize(string cm) => $"Life-size ({cm} cm)";
        public override string XrSyncAssets => "Sync model & motions";
        public override string XrSyncAssetsNote => "Takes effect on next launch.";
        public override string XrSyncNow => "Sync Now";
        public override string XrSyncing => "Syncing…";
        public override string XrSyncOffNote => "Unavailable while sync is off.";
        public override string XrSyncNowNote => "Re-fetches from the server.";
        public override string XrSyncNotStarted => "Couldn't start syncing.";
        public override string XrWalk => "Walk";
        public override string XrCursorGaze => "Eyes follow pointer";
        public override string XrResetPosition => "Reset Character Position";
        public override string XrResetAll => "Reset All Settings";
        public override string XrResetAllNote => "Keeps the server connection.";
        public override string XrResetAllConfirmNote => "Press again to reset everything.";
        public override string XrClose => "Close";

        // ── サーバーエラー ──────────────────────────────────
        public override string ErrorResponseUnreadable(string reason) => $"Couldn't read the response: {reason}";
        public override string ErrorHttp(long status) => $"The server returned an error (HTTP {status}).";
        public override string ErrorEnvOverride(string key) => $"Can't change: locked by an environment variable ({key}).";
        public override string ErrorReadonlyKey(string key) => $"This setting can't be changed ({key}).";
        public override string ErrorInvalidValue(string key) => $"Value out of range ({key}).";
        public override string ErrorUnknownKey(string key) => $"Unknown setting ({key}).";
        public override string ErrorEngineUnreachable => "Can't reach the speech engine.";
        public override string ErrorSynthesisUnavailable => "Couldn't synthesize speech.";
        public override string ErrorTtsDisabled => "Speech is disabled on the server (ttsEnabled).";
        public override string ErrorConfigUnreadable => "Couldn't read config.json, so nothing was written.";
        public override string ErrorConfigUnwritable => "Couldn't write to config.json.";
        public override string ErrorTooManyRequests => "Too many requests. Please wait a moment.";
        public override string ErrorUnknown(string error, string key) => $"{error} ({key})";
    }
}
