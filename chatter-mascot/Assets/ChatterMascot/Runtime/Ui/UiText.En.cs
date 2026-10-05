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

        public override string SectionPairing => "Connection";
        public override string XrPairOpen => "Pair…";
        public override string XrPairingPin => "PIN";
        public override string XrPairKeyboard => "Type with keyboard";
        public override string XrPairKeyboardUnavailable => "Couldn't open the keyboard.";
        public override string XrPairKeyboardInvalid => "Enter a 4-digit number.";
        public override string XrPair => "Pair";
        public override string XrPairNote => "Enter the 4-digit PIN shown on your Mac.";
        public override string XrPairing => "Pairing…";
        public override string XrPairDone => "Paired.";
        public override string XrPairWrongPin(int remaining) => $"Wrong PIN ({remaining} left).";
        public override string XrPairReissue => "Show a new PIN from \"Pair with Android…\" in the Mac menu.";
        public override string XrPairNotFound => "Couldn't find the server.";
        public override string XrPairUnreachable => "Couldn't reach the server.";
        public override string XrPairOldServer => "This server doesn't support pairing. Please update it.";
        public override string XrPairBadResponse => "Couldn't read the server's response.";
        public override string XrBack => "Back";
        public override string PairingNeededToast =>
            "Can't reach the server\nShow a PIN from \"Pair with Android…\" in the ChatterAgent menu on your Mac,\n" +
            "then enter it from \"Pair…\" in the settings panel.";
        public override string PairingDialogMessage =>
            "Enter the 4-digit PIN shown from \"Pair with Android…\" in the ChatterAgent menu on your Mac.";
        public override string Cancel => "Cancel";
    }
}
