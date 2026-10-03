namespace ChatterMascot.Ui
{
    internal sealed class EnUiText : UiText
    {
        // ── 共通 ──────────────────────────────────
        public override string Mute => "Mute";

        // ── モーションの確認 ──────────────────────────────────
        public override string NoMotionToPlay => "No motions available.";

        // ── キャラクター ──────────────────────────────────
        public override string SectionCharacter => "Character";
        public override string Size => "Size";

        // ── モーション ──────────────────────────────────
        public override string SectionMotion => "Motion";
        public override string Play => "Play";
        public override string MotionPreviewIdleOff => "Can't play while idle motion is off.";
        public override string MotionPreviewLoading => "Loading motions…";
        public override string MotionPreviewEmpty => "Put .vrma files in ~/.config/chatter-agent/animations/<emotion>/ to choose them.";
        public override string Blink => "Blink";

        // ── リセット ──────────────────────────────────
        public override string SectionReset => "Reset";

        // ── 注記・通知 ──────────────────────────────────
        public override string AppliesFromNextLaunch => "Takes effect on next launch.";

        // ── モーション再生の結果 ──────────────────────────────────
        public override string MotionPlayStarted(string id) => $"Playing {id}.";
        public override string MotionPlayBusy => "Already playing. Try again when it ends.";
        public override string MotionPlayIdleNotLoaded => "Can't play: the idle motion VRMA isn't loaded.";
        public override string MotionPlayIdleDisabled => "Idle motion is off.";
        public override string MotionPlayNotLoaded => "This motion isn't loaded.";
        public override string MotionPlayDisposed => "The character is unavailable.";

        // ── XR ──────────────────────────────────
        public override string XrLoadingModel => "Loading model…";
        public override string XrActualSize(string cm) => $"Life-size ({cm} cm)";
        public override string XrSyncAssets => "Sync model & motions";
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
    }
}
