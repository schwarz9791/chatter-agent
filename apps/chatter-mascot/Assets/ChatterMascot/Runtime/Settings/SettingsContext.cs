using System.Collections.Generic;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// 設定パネルの項目に流し込む「その時々の状態」。<c>MenuState</c>（#75）と同じ役回りで、
    /// <see cref="SettingsSchema.Build"/> の唯一の入力。
    ///
    /// ★ <b>ここに <c>MonoBehaviour</c> や <c>UnityWebRequest</c> を持ち込まないこと。</b>
    ///   スキーマを純粋関数のままにしておくと、EditMode テストから
    ///   「サーバーが落ちているときの見え方」まで固定できる。
    ///
    /// ★ <b>値の行き先は3つある</b>（→ <see cref="MascotSettings"/>）:
    ///   Unity の <c>settings.json</c> / core の <c>config.json</c> / 読むだけ。
    ///   ここは3つを1つの器にまとめているだけで、書き戻し先は
    ///   <c>SettingsPanelBridge</c> が振り分ける。
    /// </summary>
    public sealed class SettingsContext
    {
        private static readonly SettingChoice[] NoChoices = new SettingChoice[0];

        // ── Unity 側が権威を持つ値 ─────────────────────────────
        public MascotSettings Settings { get; set; } = MascotSettings.Defaults;

        /// <summary>選択中の VRM のファイル名。空なら同梱モデル</summary>
        public string VrmFileName
        {
            get { return Settings.VrmFileName; }
        }

        /// <summary>
        /// キャラクターの大きさ（＝<b>ウィンドウの倍率</b>）。1.0 が出荷値。
        ///
        /// ★★ <b><see cref="MascotSettings"/> に持たない。</b> ウィンドウの大きさは
        ///   <c>window.json</c> が持っているので、**いまの窓から読み替えて**ここに入れる
        ///   （→ <see cref="SettingsMapping.ScaleForWindow"/>）。両方に持つと権威が2つになる。
        /// </summary>
        public float WindowScale { get; set; } = 1f;

        // ── core 側が権威を持つ値 ─────────────────────────────

        /// <summary>
        /// 制御 API に繋がったか。
        ///
        /// ★★ <b>繋がらないときに項目を消さないこと。</b> <c>Enabled = false</c> +
        ///   <see cref="CoreNote"/> で出す。消すと「設定が無い」に見える。
        /// </summary>
        public bool CoreReachable { get; set; }

        /// <summary>繋がらない理由。項目の <c>note</c> に出す</summary>
        public string CoreNote { get; set; } = "サーバーに繋がりません";

        /// <summary>話者の候補。空なら取得できていない</summary>
        public IReadOnlyList<SettingChoice> Speakers { get; set; } = NoChoices;

        /// <summary>選択中の話者 ID（文字列）</summary>
        public string SpeakerId { get; set; } = "";

        public float SpeedScale { get; set; } = 1f;

        public bool SummaryEnabled { get; set; }

        /// <summary>
        /// 環境変数が勝っている core の設定キー（<c>ttsSpeakerId</c> など）。
        ///
        /// ★ 触れるように出すと <c>PATCH</c> が 409 を返すだけで、ユーザーには
        ///   「変えたのに戻る」としか見えない。**先に無効化して理由を出す**。
        /// </summary>
        public IReadOnlyCollection<string> CoreEnvOverridden { get; set; } = new string[0];

        // ── 読むだけ ─────────────────────────────────────────
        public string ProductName { get; set; } = "Chatter Mascot";
        public string Version { get; set; } = "";

        /// <summary>
        /// <c>NOTICE</c> の本文（<c>StreamingAssets/NOTICE.txt</c>）。
        ///
        /// ★ <b>C# の文字列リテラルに埋め込まないこと。</b> リポジトリの <c>NOTICE</c> と
        ///   別々に更新されて静かにズレる。読めなかったときは空のまま出す（項目は消さない）。
        /// </summary>
        public string LicenseText { get; set; } = "";

        public bool IsCoreEnvOverridden(string coreKey)
        {
            if (CoreEnvOverridden == null) return false;
            foreach (var key in CoreEnvOverridden)
            {
                if (key == coreKey) return true;
            }
            return false;
        }
    }
}
