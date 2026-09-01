using System;
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
    public readonly struct MascotSettings : IEquatable<MascotSettings>
    {
        public MascotSettings(bool muted, string muteHotKey, string hideHotKey)
        {
            Muted = muted;
            MuteHotKey = muteHotKey;
            HideHotKey = hideHotKey;
        }

        public bool Muted { get; }

        /// <summary>
        /// ミュートのショートカット。既定は <see cref="HotKeySpec.Default"/>。
        ///
        /// ★ <b>既定値をここに書き写さないこと。</b> 実際に一度ずれた ——
        ///   <c>⌥M</c> と書いてあるのに既定は <c>⌃⌥M</c> で、しかも <c>⌥M</c> は
        ///   <b>この doc を含む変更が「文字を入力するから」と結論して外した</b>組み合わせだった。
        /// </summary>
        public string MuteHotKey { get; }

        /// <summary>
        /// キャラクターの表示を切り替えるショートカット。既定は <see cref="HotKeySpec.DefaultHide"/>。
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

        /// <summary>
        /// ★★ <b>プロパティを足したらここにも足すこと。</b> 実際に一度落とした ——
        ///   <c>HideHotKey</c> を比べ忘れたせいで <c>SettingsStore.Refresh</c> が
        ///   「変わっていない」と返し、<c>StatusItemBridge</c> が古い値を持ったまま
        ///   次の保存で<b>ユーザーの編集をディスクから消した</b>。
        ///
        /// ★ <b>足し忘れは <c>MascotSettingsTests</c> が落ちて教える</b>
        ///   （リフレクションで全プロパティを回している）。#76 で項目が増えるときの保険。
        /// </summary>
        public bool Equals(MascotSettings other)
        {
            return Muted == other.Muted
                && string.Equals(MuteHotKey, other.MuteHotKey, StringComparison.Ordinal)
                && string.Equals(HideHotKey, other.HideHotKey, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is MascotSettings && Equals((MascotSettings)obj);
        }

        public override int GetHashCode()
        {
            var hash = Muted ? 1 : 0;
            hash = (hash * 397) ^ (MuteHotKey != null ? MuteHotKey.GetHashCode() : 0);
            hash = (hash * 397) ^ (HideHotKey != null ? HideHotKey.GetHashCode() : 0);
            return hash;
        }
    }
}
