using ChatterMascot.Ui;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// <c>~/.config/chatter-agent/mascot/settings.json</c> が持つ値。
    ///
    /// ★ <b>いまは2つだけ。</b> 項目が増えるのは設定 UI（#76）で、
    ///   そのとき<b>並び順を持つのはスキーマの側</b>になる。ここは値の器に留める。
    ///
    /// ★ <b>「キャラクターを隠す」を入れないこと。</b> 隠した状態を永続化すると、
    ///   次の起動で「マスコットが出ない」に化ける。ミュートはアイコンが薄くなるので
    ///   気づけるが、隠れているものは気づきようが無い。
    /// </summary>
    public readonly struct MascotSettings
    {
        public MascotSettings(bool muted, string muteHotKey)
        {
            Muted = muted;
            MuteHotKey = muteHotKey;
        }

        public bool Muted { get; }

        /// <summary>→ <see cref="HotKeySpec"/>。既定は <c>"opt+m"</c></summary>
        public string MuteHotKey { get; }

        public static MascotSettings Defaults
        {
            get { return new MascotSettings(false, HotKeySpec.Default); }
        }

        public MascotSettings WithMuted(bool muted)
        {
            return new MascotSettings(muted, MuteHotKey);
        }
    }
}
