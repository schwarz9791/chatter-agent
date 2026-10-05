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

        // ── 共通 ──────────────────────────────────
        public abstract string Mute { get; }

        // ── モーションの確認 ──────────────────────────────────
        public abstract string NoMotionToPlay { get; }

        // ── キャラクター ──────────────────────────────────
        public abstract string SectionCharacter { get; }
        public abstract string Size { get; }

        // ── モーション ──────────────────────────────────
        public abstract string SectionMotion { get; }
        public abstract string Play { get; }
        public abstract string MotionPreviewIdleOff { get; }
        public abstract string MotionPreviewLoading { get; }
        public abstract string MotionPreviewEmpty { get; }
        public abstract string Blink { get; }

        // ── リセット ──────────────────────────────────
        public abstract string SectionReset { get; }

        // ── 注記・通知 ──────────────────────────────────
        public abstract string AppliesFromNextLaunch { get; }

        // ── モーション再生の結果 ──────────────────────────────────
        public abstract string MotionPlayStarted(string id);
        public abstract string MotionPlayBusy { get; }
        public abstract string MotionPlayIdleNotLoaded { get; }
        public abstract string MotionPlayIdleDisabled { get; }
        public abstract string MotionPlayNotLoaded { get; }
        public abstract string MotionPlayDisposed { get; }

        // ── XR ──────────────────────────────────
        public abstract string XrLoadingModel { get; }
        public abstract string XrActualSize(string cm);
        public abstract string XrSyncAssets { get; }
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

        // ── XR: 接続（ペアリング） ──────────────────────────────────
        public abstract string SectionPairing { get; }
        public abstract string XrPairOpen { get; }
        public abstract string XrPairingPin { get; }
        public abstract string XrPairKeyboard { get; }
        public abstract string XrPairKeyboardUnavailable { get; }
        public abstract string XrPairKeyboardInvalid { get; }
        public abstract string XrPair { get; }
        public abstract string XrPairNote { get; }
        public abstract string XrPairing { get; }
        public abstract string XrPairDone { get; }
        public abstract string XrPairWrongPin(int remaining);
        public abstract string XrPairReissue { get; }
        public abstract string XrPairNotFound { get; }
        public abstract string XrPairUnreachable { get; }
        public abstract string XrPairOldServer { get; }
        public abstract string XrPairBadResponse { get; }
        public abstract string XrBack { get; }
        public abstract string ServerUnreachableToast { get; }
        public abstract string PairingNeededToast { get; }
        public abstract string PairingDialogMessage { get; }
        public abstract string Cancel { get; }
    }
}
