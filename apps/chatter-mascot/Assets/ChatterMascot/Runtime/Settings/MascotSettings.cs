using System;
using ChatterMascot.Ui;

namespace ChatterMascot.Settings
{
    /// <summary>
    /// <c>~/.config/chatter-agent/mascot/settings.json</c> が持つ値。
    ///
    /// ★ <b>ここに入るのは Unity 側が権威を持つ値だけ。</b> 音声スタイル・話速・要約の ON/OFF は
    ///   core の <c>config.json</c> が持ち、<c>PATCH /v1/config</c> 経由で書く
    ///   （→ <see cref="Net.CoreConfigClient"/>）。★ <b>Unity から <c>config.json</c> を
    ///   直接書かないこと</b> —— あちらの <c>SPECS</c> のパーサを通らない JSON は誰にも検証されない。
    ///
    /// ★ <b>音量は Unity・話速は core。</b> 紛らわしいが理由がある —— 音量は<b>再生側のつまみ</b>で
    ///   合成し直さなくても効き、クライアントごとに違ってよい（デスクトップと XR グラスで
    ///   同じ音量である必要は無い）。話速は<b>合成のパラメータ</b>で、
    ///   <c>audio_query</c> の <c>speedScale</c> を変えない限り WAV が変わらない。
    ///
    /// ★★ <b>キャラクターの大きさをここに持たないこと。</b> あれは<b>ウィンドウの大きさ</b>で、
    ///   <c>window.json</c> が既に持っている。両方に持つと権威が2つになり、ユーザーが窓を
    ///   直接リサイズしたときどちらが勝つのか説明できない
    ///   （→ <see cref="SettingsMapping.ScaleForWindow"/>）。
    ///
    /// ★ <b>「キャラクターを隠す」を入れないこと。</b> 隠した状態を永続化すると、
    ///   次の起動で「マスコットが出ない」に化ける。ミュートはアイコンが薄くなるので
    ///   気づけるが、隠れているものは気づきようが無い。
    ///
    /// ★★ <b>プロパティを足したら <see cref="Copy"/> と <see cref="Equals"/> の両方に足すこと。</b>
    ///   <c>MascotSettingsTests</c> がリフレクションで全プロパティを回し、
    ///   <c>With&lt;プロパティ名&gt;</c> が無いか、<c>Equals</c> に効いていないかを落として教える。
    ///   実際に一度落とした —— <c>HideHotKey</c> を比べ忘れたせいで <c>SettingsStore.Refresh</c> が
    ///   「変わっていない」と返し、次の保存で<b>ユーザーの編集をディスクから消した</b>。
    /// </summary>
    public readonly struct MascotSettings : IEquatable<MascotSettings>
    {
        /// <summary>
        /// ★ <b>直接呼ばないこと。</b> 引数の数だけ順番を間違える余地があるので、
        ///   <see cref="Defaults"/> から <c>With*</c> で組み立てる。
        /// </summary>
        private MascotSettings(
            bool muted, string muteHotKey, string hideHotKey,
            float volume,
            bool idleMotion, bool cursorGaze, bool blink,
            string vrmFileName,
            int frameRate)
        {
            Muted = muted;
            MuteHotKey = muteHotKey;
            HideHotKey = hideHotKey;
            Volume = volume;
            IdleMotion = idleMotion;
            CursorGaze = cursorGaze;
            Blink = blink;
            VrmFileName = vrmFileName;
            FrameRate = frameRate;
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

        /// <summary>
        /// 再生音量。<b>0.0〜1.0</b>（画面には 0〜100% で出る）。
        /// ★ 1.0 を上限にしている理由は <see cref="SettingsMapping.VolumeMax"/> に。
        ///
        /// ★ macOS では <c>afplay -v</c>、Android では <c>AudioSource.volume</c> に効く。
        ///   ★ <b>等倍のときは <c>afplay</c> の引数を増やさない</b>
        ///   （→ <see cref="SettingsMapping.NeedsVolumeArgument"/>）。
        /// </summary>
        public float Volume { get; }

        /// <summary>
        /// 待機モーションを回すか。
        ///
        /// ★ <b>VRMA と手続き的アイドルを1つに畳んである。</b> あの2実装は
        ///   「片方が読めないときのフォールバック」でしかなく、ユーザーから見て
        ///   「待機モーション」は1つの概念。
        /// </summary>
        public bool IdleMotion { get; }

        /// <summary>マウスカーソルを目で追うか（<c>VrmCharacter.cursorGaze</c>）</summary>
        public bool CursorGaze { get; }

        /// <summary>自動まばたきを回すか（<c>VrmCharacter.blinkEnabled</c>）</summary>
        public bool Blink { get; }

        /// <summary>
        /// 選んだ VRM の<b>元のファイル名</b>（<c>character.vrm</c>）。
        ///
        /// ★★ <b>これは表示のための札で、探索には使わない。</b> 実ファイルは
        ///   <c>models/</c> に<b>固定名</b>で置かれる（<c>Vrm.AssetPath.SelectedVrmFile</c>）。
        ///   ここに名前を持たせて探索させると、<b>設定と実ファイルがズレたときに直せない</b>
        ///   ——実際に「設定は覚えているのに誰も読んでいない」状態を実機で踏んだ。
        ///
        /// ★ 空なら「同梱のモデルを使っています」と出す。
        /// </summary>
        public string VrmFileName { get; }

        /// <summary>
        /// 表示のフレームレート上限（<b>30 か 60</b>。→ <see cref="SettingsMapping.FrameRateChoices"/>）。
        ///
        /// ★ <b>デスクトップだけの項目。</b> 設定パネル（#88）が書き、<c>MascotRunner</c> の
        ///   <c>FrameRateBudget.SetBaseline</c> に反映される。Android / XR には設定パネルが無いので、
        ///   この JSON の既定（<see cref="SettingsMapping.DefaultFrameRate"/>）がそのまま使われる
        ///   （→ <c>MascotRunner.targetFrameRate</c> の doc）。
        /// </summary>
        public int FrameRate { get; }

        public static MascotSettings Defaults
        {
            get
            {
                return new MascotSettings(
                    false, HotKeySpec.Default, HotKeySpec.DefaultHide,
                    1f,
                    true, true, true,
                    "",
                    SettingsMapping.DefaultFrameRate);
            }
        }

        /// <summary>
        /// 指定したものだけ差し替えた値を返す。<b>全フィールドを列挙する唯一の場所</b>。
        ///
        /// ★ <c>With*</c> をここへ集約しているのは、フィールドを足したときに
        ///   <b>直す場所が1つで済む</b>ようにするため。個々の <c>With*</c> が
        ///   全フィールドを並べる形にすると、足し忘れが N 箇所に散る。
        /// </summary>
        private MascotSettings Copy(
            bool? muted = null, string muteHotKey = null, string hideHotKey = null,
            float? volume = null,
            bool? idleMotion = null, bool? cursorGaze = null, bool? blink = null,
            string vrmFileName = null,
            int? frameRate = null)
        {
            return new MascotSettings(
                muted ?? Muted,
                muteHotKey ?? MuteHotKey,
                hideHotKey ?? HideHotKey,
                volume ?? Volume,
                idleMotion ?? IdleMotion,
                cursorGaze ?? CursorGaze,
                blink ?? Blink,
                vrmFileName ?? VrmFileName,
                frameRate ?? FrameRate);
        }

        public MascotSettings WithMuted(bool value) => Copy(muted: value);
        public MascotSettings WithMuteHotKey(string value) => Copy(muteHotKey: value);
        public MascotSettings WithHideHotKey(string value) => Copy(hideHotKey: value);
        public MascotSettings WithVolume(float value) => Copy(volume: value);
        public MascotSettings WithIdleMotion(bool value) => Copy(idleMotion: value);
        public MascotSettings WithCursorGaze(bool value) => Copy(cursorGaze: value);
        public MascotSettings WithBlink(bool value) => Copy(blink: value);
        public MascotSettings WithVrmFileName(string value) => Copy(vrmFileName: value);
        public MascotSettings WithFrameRate(int value) => Copy(frameRate: value);

        /// <summary>
        /// ★★ <b>プロパティを足したらここにも足すこと</b>（→ 型の doc）。
        ///   足し忘れは <c>MascotSettingsTests</c> が落として教える。
        /// </summary>
        public bool Equals(MascotSettings other)
        {
            return Muted == other.Muted
                && string.Equals(MuteHotKey, other.MuteHotKey, StringComparison.Ordinal)
                && string.Equals(HideHotKey, other.HideHotKey, StringComparison.Ordinal)
                // ★ float は == で比べてよい。ここで比べているのは「保存された値が変わったか」で、
                //   両辺とも同じ経路（刻みへの丸め）を通った値なので、近似の一致は要らない
                && Volume.Equals(other.Volume)
                && IdleMotion == other.IdleMotion
                && CursorGaze == other.CursorGaze
                && Blink == other.Blink
                && string.Equals(VrmFileName, other.VrmFileName, StringComparison.Ordinal)
                && FrameRate == other.FrameRate;
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
            hash = (hash * 397) ^ Volume.GetHashCode();
            hash = (hash * 397) ^ (IdleMotion ? 1 : 0);
            hash = (hash * 397) ^ (CursorGaze ? 1 : 0);
            hash = (hash * 397) ^ (Blink ? 1 : 0);
            hash = (hash * 397) ^ (VrmFileName != null ? VrmFileName.GetHashCode() : 0);
            hash = (hash * 397) ^ FrameRate;
            return hash;
        }
    }
}
