namespace ChatterMascot.Ui
{
    internal sealed class JaUiText : UiText
    {
        // ── メニュー ──────────────────────────────────
        public override string MenuMute => "ミュート";
        public override string MenuHide => "キャラクターを隠す";
        public override string MenuShow => "キャラクターを表示する";
        public override string MenuSettings => "設定を開く…";
        public override string MenuQuit => "終了";
        public override string About(string product) => $"{product} について";
        public override string MenuWithShortcut(string label, string symbols) => $"{label}（{symbols}）";

        // ── 設定パネル: 共通 ──────────────────────────────────
        public override string SettingsTitle(string product) => $"{product} の設定";
        public override string PanelRecord => "記録";
        public override string PanelCancel => "中止";
        public override string PanelRecording => "キーを押す";
        public override string PanelEmpty => "（取得できません）";
        public override string CoreUnreachable => "サーバーに繋がりません";
        public override string NoMotionToPlay => "選べるモーションがありません";

        // ── 設定パネル: キャラクター ──────────────────────────────────
        public override string SectionCharacter => "キャラクター";
        public override string ChooseVrm => "VRM モデルを選ぶ…";
        public override string BundledModelNote => "同梱のモデルを使っています";
        public override string Size => "大きさ";

        // ── 設定パネル: オーディオ ──────────────────────────────────
        public override string SectionAudio => "オーディオ";
        public override string VoiceStyle => "音声スタイル";
        public override string Volume => "音量";
        public override string SpeakingSpeed => "話す速さ";
        public override string PlayTestVoice => "テスト音声を再生";
        public override string SpeakerListUnavailable => "話者の一覧を取得できませんでした";
        public override string AppliesFromNextSentence => "次に喋る文から変わります";
        public override string EnvOverridden(string envName) => $"環境変数（{envName}）で固定されています";

        // ── 設定パネル: モーション ──────────────────────────────────
        public override string SectionMotion => "モーション";
        public override string IdleMotion => "待機モーション";
        public override string PreviewMotion => "モーションを確認";
        public override string Play => "再生";
        public override string MotionPreviewIdleOff => "待機モーションが OFF の間は再生できません";
        public override string MotionPreviewLoading => "モーションを読み込み中です";
        public override string MotionPreviewEmpty => "~/.config/chatter-agent/animations/<感情>/ に .vrma を置くと選べます";
        public override string CursorGaze => "カーソルを目で追う";
        public override string Blink => "まばたき";
        public override string FrameRate => "フレームレート";
        public override string FrameRateNote => "60 fps は CPU 使用率が上がります";

        // ── 設定パネル: AI要約 ──────────────────────────────────
        public override string SectionAiSummary => "AI要約";
        public override string SummarizeLongMessages => "長いメッセージを要約してから読み上げる";
        public override string SummarizeNote => "要約には時間がかかります（間に合わなければ原文を読み上げます）";
        public override string SummaryEngine => "要約エンジン";
        public override string SummaryEngineNote => "fm は macOS 27 以降で使えます。使えないときは原文を読み上げます";

        // ── 設定パネル: 感情判定 ──────────────────────────────────
        public override string SectionEmotion => "感情判定";
        public override string EmotionEngine => "感情判定エンジン";
        public override string EmotionEngineNote => "fm は macOS 27 以降で使えます。使えないときは辞書式に戻ります";
        public override string EmotionDictionary => "辞書式";

        // ── 設定パネル: ショートカット ──────────────────────────────────
        public override string SectionShortcuts => "ショートカット";
        public override string ShortcutsNote => "「記録」を押してキーを押してください（修飾キーを1つ以上）";
        public override string HotKeyMute => "ミュートの切り替え";
        public override string HotKeyHide => "キャラクターの表示切り替え";
        public override string HotKeyClashNote(string label) => $"「{label}」と同じ組み合わせなので登録できません";
        public override string HotKeyClashRejected(string label) => $"「{label}」と同じ組み合わせです";
        public override string HotKeyNeedsModifier => "修飾キー（⌃ ⌥ ⇧ ⌘）を一緒に押してください";
        public override string HotKeyKeyNotAllowed => "このキーはショートカットに使えません";
        public override string HotKeyRecordedEmpty => "記録の中身が空です";
        public override string HotKeyRecordedUnreadable(string text) => $"記録の形が読めません: \"{text}\"";

        // ── 設定パネル: リセット ──────────────────────────────────
        public override string SectionReset => "リセット";
        public override string ResetPosition => "キャラクターの位置と大きさをリセット";
        public override string ResetAll => "すべての設定をリセット…";
        public override string ResetAllNote => "選んだモデルのファイルも消して、完全に初期状態へ戻します";
        public override string Quit => "終了";
        public override string PositionReset => "位置と大きさを既定に戻しました";

        // ── 設定パネル: 注記・通知 ──────────────────────────────────
        public override string SpeakerIdUnreadable => "話者 ID を読めませんでした";
        public override string PlaybackNotReady => "再生の準備ができていません";
        public override string MutedNoSound => "ミュート中なので鳴りません";
        public override string AppliesFromNextLaunch => "次に起動したときから反映されます";

        // ── 設定パネル: 「について」 ──────────────────────────────────
        public override string Version => "バージョン";
        public override string License => "ライセンス";

        // ── 確認ダイアログ ──────────────────────────────────
        public override string ConfirmResetTitle => "すべての設定をリセットしますか？";
        public override string ConfirmResetMessage => "大きさ・位置・音量・モーション・ショートカット・音声スタイル・話す速さ・要約・要約エンジン・感情判定の設定が既定に戻り、選んだ VRM モデルのファイルも削除されます。この操作は取り消せません。";
        public override string ConfirmResetOk => "リセットする";
        public override string ConfirmResetCancel => "やめる";
        public override string ResetDone(int removedModels) =>
            removedModels > 0 ? $"既定に戻しました（モデル {removedModels} 件を削除）" : "既定に戻しました";
        public override string ResetModelsFailed(string reason) => $"モデルを消せませんでした: {reason}";
        public override string ResetCoreFailed(string reason) => $"音声スタイル・話す速さ・要約などは戻せませんでした（{reason}）";
        public override string ResetCoreDefaultsUnavailable => "既定値を取れません";
        public override string ResetKeyFailed(string key, string reason) => $"{key} を戻せませんでした（{reason}）";

        // ── VRM の選択 ──────────────────────────────────
        public override string ChooseVrmTitle => "VRM モデルを選ぶ";
        public override string ChooseVrmMessage => "選んだファイルは models/ にコピーされます";
        public override string ChooseVrmButton => "選ぶ";
        public override string NativePluginMissing => "ネイティブプラグインが無いのでファイルを選べません";
        public override string SettingsFolderUnknown => "設定の置き場所を決められませんでした";
        public override string FileNameUnreadable => "ファイル名を読めませんでした";
        public override string CopyFailed(string reason) => $"コピーできませんでした: {reason}";

        // ── モーション再生の結果 ──────────────────────────────────
        public override string MotionPlayStarted(string id) => $"{id} を再生します";
        public override string MotionPlayBusy => "再生中です。終わってからもう一度押してください";
        public override string MotionPlayIdleNotLoaded => "待機モーションの VRMA が読めていないので再生できません";
        public override string MotionPlayIdleDisabled => "待機モーションが OFF です";
        public override string MotionPlayNotLoaded => "このモーションは読み込めていません";
        public override string MotionPlayDisposed => "キャラクターが無効です";

        // ── XR ──────────────────────────────────
        public override string XrMute => "ミュート";
        public override string XrLoadingModel => "モデルを読み込んでいます";
        public override string XrActualSize(string cm) => $"実寸（{cm} cm）";
        public override string XrSyncAssets => "モデルとモーションを同期";
        public override string XrSyncAssetsNote => "次回の起動から反映されます";
        public override string XrSyncNow => "今すぐ同期";
        public override string XrSyncing => "同期しています…";
        public override string XrSyncOffNote => "同期が OFF の間は使えません";
        public override string XrSyncNowNote => "サーバーから取り直します";
        public override string XrSyncNotStarted => "同期を始められませんでした";
        public override string XrWalk => "歩く";
        public override string XrCursorGaze => "指している先を目で追う";
        public override string XrResetPosition => "キャラクターの位置をリセット";
        public override string XrResetAll => "すべての設定をリセット";
        public override string XrResetAllNote => "接続先は残します";
        public override string XrResetAllConfirmNote => "もう一度押すとすべての設定をリセットします";
        public override string XrClose => "閉じる";

        // ── サーバーエラー ──────────────────────────────────
        public override string ErrorResponseUnreadable(string reason) => $"応答を読めませんでした: {reason}";
        public override string ErrorHttp(long status) => $"エラーが返りました（HTTP {status}）";
        public override string ErrorEnvOverride(string key) => $"環境変数で固定されているので変えられません（{key}）";
        public override string ErrorReadonlyKey(string key) => $"この設定は変更できません（{key}）";
        public override string ErrorInvalidValue(string key) => $"値が範囲外です（{key}）";
        public override string ErrorUnknownKey(string key) => $"知らない設定です（{key}）";
        public override string ErrorEngineUnreachable => "音声合成エンジンに繋がりません";
        public override string ErrorSynthesisUnavailable => "音声を合成できませんでした";
        public override string ErrorTtsDisabled => "サーバー側で音声が無効になっています（ttsEnabled）";
        public override string ErrorConfigUnreadable => "config.json を読めないので書き込みませんでした";
        public override string ErrorConfigUnwritable => "config.json に書けませんでした";
        public override string ErrorTooManyRequests => "続けて押しすぎです。少し待ってください";
        public override string ErrorUnknown(string error, string key) => $"{error}（{key}）";
    }
}
