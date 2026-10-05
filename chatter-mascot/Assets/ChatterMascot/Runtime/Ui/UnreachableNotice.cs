namespace ChatterMascot.Ui
{
    /// <summary>接続できないときに端末へ出す案内の種類。</summary>
    public enum UnreachableNotice
    {
        /// <summary>サーバーの起動状態の確認とアプリの再起動を促すトースト。</summary>
        ServerDown,

        /// <summary>ペアリングの入力を促すダイアログ（設定パネルの無い端末）。</summary>
        PairingPrompt,

        /// <summary>設定パネルでのペアリングを促すトースト。</summary>
        PairingToast,
    }

    /// <summary>繋がらないときの案内の選び方。描画にも時刻にも依存しない。</summary>
    public static class UnreachableNotices
    {
        /// <summary>
        /// ★ トークンが無いなら、再起動ではなくペアリングが打てる手。ただし一度繋がっていれば
        ///   接続先は合っている（adb reverse の経路を含む）ので、ペアリングしても直らない。
        /// </summary>
        public static UnreachableNotice Decide(bool hasToken, bool connectedOnce, bool canPrompt)
        {
            if (hasToken || connectedOnce) return UnreachableNotice.ServerDown;
            return canPrompt ? UnreachableNotice.PairingPrompt : UnreachableNotice.PairingToast;
        }
    }
}
