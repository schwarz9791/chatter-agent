namespace ChatterMascot.Ui
{
    internal sealed class JaUiText : UiText
    {
        // ── 共通 ──────────────────────────────────
        public override string Mute => "ミュート";

        // ── モーションの確認 ──────────────────────────────────
        public override string NoMotionToPlay => "選べるモーションがありません";

        // ── キャラクター ──────────────────────────────────
        public override string SectionCharacter => "キャラクター";
        public override string Size => "大きさ";

        // ── モーション ──────────────────────────────────
        public override string SectionMotion => "モーション";
        public override string Play => "再生";
        public override string MotionPreviewIdleOff => "待機モーションが OFF の間は再生できません";
        public override string MotionPreviewLoading => "モーションを読み込み中です";
        public override string MotionPreviewEmpty => "~/.config/chatter-agent/animations/<感情>/ に .vrma を置くと選べます";
        public override string Blink => "まばたき";

        // ── リセット ──────────────────────────────────
        public override string SectionReset => "リセット";

        // ── 注記・通知 ──────────────────────────────────
        public override string AppliesFromNextLaunch => "次回の起動から反映されます";

        // ── モーション再生の結果 ──────────────────────────────────
        public override string MotionPlayStarted(string id) => $"{id} を再生します";
        public override string MotionPlayBusy => "再生中です。終わってからもう一度押してください";
        public override string MotionPlayIdleNotLoaded => "待機モーションの VRMA が読めていないので再生できません";
        public override string MotionPlayIdleDisabled => "待機モーションが OFF です";
        public override string MotionPlayNotLoaded => "このモーションは読み込めていません";
        public override string MotionPlayDisposed => "キャラクターが無効です";

        // ── XR ──────────────────────────────────
        public override string XrLoadingModel => "モデルを読み込んでいます";
        public override string XrActualSize(string cm) => $"実寸（{cm} cm）";
        public override string XrSyncAssets => "モデルとモーションを同期";
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
    }
}
