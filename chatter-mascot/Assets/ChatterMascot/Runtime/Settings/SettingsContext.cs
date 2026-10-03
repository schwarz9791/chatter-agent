using System.Collections.Generic;
using ChatterMascot.Ui;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// 設定パネルの項目に流し込む「その時々の状態」。
    /// <see cref="SettingsSchema.BuildXr"/> の唯一の入力。
    ///
    /// ★ <b>ここに <c>MonoBehaviour</c> や <c>UnityWebRequest</c> を持ち込まないこと。</b>
    ///   スキーマを純粋関数のままにしておくと、EditMode テストから
    ///   「サーバーが落ちているときの見え方」まで固定できる。
    /// </summary>
    public sealed class SettingsContext
    {
        /// <summary>画面に出す文言の表。既定は日本語（→ <see cref="UiText.For"/>）</summary>
        public UiText Text { get; set; } = UiText.Ja;

        // ── Unity 側が権威を持つ値 ─────────────────────────────
        public MascotSettings Settings { get; set; } = MascotSettings.Defaults;

        /// <summary>
        /// 「モーションを確認」の選択肢。<c>SettingsSchema.MotionPreviewChoices</c> が
        /// <c>VrmCharacter.MotionClips</c> から変換したものをそのまま持つ。
        ///
        /// ★★ <b>既定は <c>null</c>。空配列にしないこと。</b> <c>null</c> は「マニフェストが
        ///   まだ読み込まれていない」、空配列は「読み込んだが1本も無い」——別の状態で、
        ///   出す note も無効化の理由も違う（→ <see cref="SettingsSchema.BuildXr"/>）。
        /// </summary>
        public IReadOnlyList<SettingChoice> MotionClips { get; set; }

        /// <summary>
        /// XR の「大きさ」の選択肢（→ <see cref="SettingsMapping.XrHeightSteps"/> ＋
        /// <see cref="SettingsMapping.XrHeightChoices"/>）。
        ///
        /// ★★ <b>既定は <c>null</c>。空配列にしないこと。</b> <see cref="MotionClips"/> と同じ
        ///   規約——段は読み込んだモデルの実寸から作るので、モデルが読めるまで <c>null</c>
        ///   （「読み込み中」）のまま。
        /// </summary>
        public IReadOnlyList<SettingChoice> XrHeightChoices { get; set; }

        /// <summary>モデルとモーションの同期が走っているか。XR だけが使う。</summary>
        public bool AssetSyncRunning { get; set; }

        /// <summary>
        /// 「モーションを確認」で選択中の id（<c>"idle/Hub_Idle01.vrma"</c> の形）。
        ///
        /// ★★ <b>保存しない。</b> <c>settings.json</c> にも書かない——ここは確認用の
        ///   一時的な選択で、本番の再生（文の <c>emotion</c> から自動で選ぶ）とは別物。
        ///   パネルを閉じたら忘れてよい。
        /// </summary>
        public string MotionPreview { get; set; } = "";
    }
}
