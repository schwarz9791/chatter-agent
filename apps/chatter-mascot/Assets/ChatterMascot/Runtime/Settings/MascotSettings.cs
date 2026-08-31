using ChatterMascot.Ui;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// <c>~/.config/chatter-agent/mascot/settings.json</c> が持つ値。
    ///
    /// ★ <b>いまはショートカット2本とミュートの状態だけ。</b> 項目が増えるのは設定 UI（#76）で、
    ///   そのとき<b>並び順を持つのはスキーマの側</b>になる。ここは値の器に留める。
    ///
    /// ★ <b>「キャラクターを隠す」を入れないこと。</b> 隠した状態を永続化すると、
    ///   次の起動で「マスコットが出ない」に化ける。ミュートはアイコンが薄くなるので
    ///   気づけるが、隠れているものは気づきようが無い。
    /// </summary>
    public readonly struct MascotSettings
    {
        public MascotSettings(bool muted, string muteHotKey, string hideHotKey)
        {
            Muted = muted;
            MuteHotKey = muteHotKey;
            HideHotKey = hideHotKey;
        }

        public bool Muted { get; }

        /// <summary>→ <see cref="HotKeySpec"/>。既定は <c>"opt+m"</c></summary>
        public string MuteHotKey { get; }

        /// <summary>
        /// キャラクターの表示を切り替えるショートカット。既定は <c>"opt+h"</c>。
        ///
        /// ★ <b>ここに入るのはショートカットの<u>設定</u>だけで、隠している<u>状態</u>ではない</b>
        ///   （→ 型の doc）。
        /// </summary>
        public string HideHotKey { get; }

        public static MascotSettings Defaults
        {
            get { return new MascotSettings(false, HotKeySpec.Default, HotKeySpec.DefaultHide); }
        }

        public MascotSettings WithMuted(bool muted)
        {
            return new MascotSettings(muted, MuteHotKey, HideHotKey);
        }
    }
}
