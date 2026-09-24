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
            int frameRate,
            string serverUrl, string token, string assetSync,
            float xrHeight, float xrLegacyScale, float xrDistance, float xrAzimuth, float xrFeetBelowEye,
            bool walk)
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
            ServerUrl = serverUrl;
            Token = token;
            AssetSync = assetSync;
            XrHeight = xrHeight;
            XrLegacyScale = xrLegacyScale;
            XrDistance = xrDistance;
            XrAzimuth = xrAzimuth;
            XrFeetBelowEye = xrFeetBelowEye;
            Walk = walk;
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
        /// ★ 書くのはデスクトップの設定パネル（#88）だけで、<c>MascotRunner</c> の
        ///   <c>FrameRateBudget.SetBaseline</c> に反映される。<b>Android では反映しない</b>
        ///   （→ <see cref="SettingsMapping.AppliesFrameRate"/>）——この値が <c>settings.json</c> に
        ///   書かれていても、シーンの <c>[SerializeField]</c> の既定がそのまま使われる
        ///   （→ <c>MascotRunner.targetFrameRate</c> の doc）。
        /// </summary>
        public int FrameRate { get; }

        /// <summary>
        /// 接続先（<c>ws://</c> / <c>wss://</c> の絶対 URL）。空なら未指定（→ <c>MascotRunner</c> の既定 /
        /// 起動引数に譲る）。
        ///
        /// ★★ <b>起動時に1回だけ読まれる</b>（<see cref="MascotRunner.ResolveServerUrl"/>）。
        ///   ファイルを書き換えても、<b>次回の起動まで反映されない</b> ——
        ///   接続を1回きり捕まえる設計（→ <c>MascotRunner.ServerUrl</c> の doc）を保つため。
        /// </summary>
        public string ServerUrl { get; }

        /// <summary>
        /// 非ループバックの接続に要る共有トークン。空なら未指定。
        ///
        /// ★ <see cref="ServerUrl"/> と同じく<b>起動時に1回だけ</b>読まれる。
        /// </summary>
        public string Token { get; }

        /// <summary>
        /// サーバーから <c>models/</c> / <c>animations/</c> を取りに行くか
        /// （<c>"auto"</c> / <c>"off"</c>、既定 <c>"auto"</c>）。
        ///
        /// ★ デスクトップでは同期しない——サーバーと同じファイルシステムを直接読んでいるので
        ///   意味が無い（<c>MascotRunner</c> が <c>AssetEnvFactory.HasUserConfigDirectory</c> で
        ///   落とす）。ここは Android 側で人手に止めたいときの逃げ道。
        /// ★ <see cref="ServerUrl"/> と同じく<b>起動時に1回だけ</b>読まれる。反映は次回の起動から。
        /// ★★ デスクトップの設定パネル（<c>SettingsSchema</c>）には出さないこと——
        ///   デスクトップは同期しないので、出しても押す意味の無い項目になる。
        /// </summary>
        public string AssetSync { get; }

        /// <summary>
        /// Android XR でのキャラクターの大きさ（cm。読み込んだモデルの実際の高さに対する目標値）。
        ///
        /// ★ <b>デスクトップの「キャラクターの大きさ」（<see cref="SettingsMapping.ScaleMin"/> ほか）
        ///   とは別概念。</b> あちらはウィンドウの倍率、こちらは実寸の高さ。
        /// ★ <b>実寸を超える値は持たせない。</b> 段への丸め・実寸へのクランプは
        ///   <see cref="SettingsMapping.NearestXrHeight"/> が行う——ここは丸め後の値をそのまま持つだけ。
        /// ★ デスクトップの設定パネルには出さない。既定は <see cref="SettingsMapping.XrDefaultHeight"/>。
        /// </summary>
        public float XrHeight { get; }

        /// <summary>
        /// <c>xr.scale</c> だけを持つ古い <c>settings.json</c> を読んだときの、
        /// 未換算の倍率。<b>0 は「無い」</b>（→ <see cref="XrHeight"/> が既に確定している）。
        ///
        /// ★★ <b>ここに実寸換算後の値を書き戻さないこと。</b> 換算にはモデルの実際の高さが要り、
        ///   それは Runtime 層の外（XR 側でモデルを読み込んだ後）でしか分からない。換算できたら
        ///   <see cref="XrHeight"/> を確定値にして、ここは 0 に戻す（
        ///   <c>WithXrHeight(...).WithXrLegacyScale(0f)</c>）。
        /// ★ 0 を「無い」に使うのは <see cref="VrmFileName"/> の空文字と同じ流儀
        ///   （<c>xr.scale</c> の有効範囲は 0 より大きいので、0 に実際の値が来ることは無い）。
        /// </summary>
        public float XrLegacyScale { get; }

        /// <summary>Android XR での、目からキャラまでの水平距離（メートル）。空間固定を組む起動時に1回だけ使う。</summary>
        public float XrDistance { get; }

        /// <summary>Android XR での、起動時の正面から右回りに何度の方向へキャラを置くか。</summary>
        public float XrAzimuth { get; }

        /// <summary>Android XR での、キャラの足元が目より何 m 下か。</summary>
        public float XrFeetBelowEye { get; }

        /// <summary>
        /// Android XR で歩行範囲の円を出し、指した先へ歩かせるか。既定は <b>true</b>。
        ///
        /// ★ デスクトップの設定パネルには出さない（デスクトップは歩かない）。
        /// </summary>
        public bool Walk { get; }

        public static MascotSettings Defaults
        {
            get
            {
                return new MascotSettings(
                    false, HotKeySpec.Default, HotKeySpec.DefaultHide,
                    1f,
                    true, true, true,
                    "",
                    SettingsMapping.DefaultFrameRate,
                    "", "", SettingsMapping.DefaultAssetSync,
                    SettingsMapping.XrDefaultHeight, 0f, SettingsMapping.XrDefaultDistance,
                    SettingsMapping.XrDefaultAzimuth, SettingsMapping.XrDefaultFeetBelowEye,
                    true);
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
            int? frameRate = null,
            string serverUrl = null, string token = null, string assetSync = null,
            float? xrHeight = null, float? xrLegacyScale = null,
            float? xrDistance = null, float? xrAzimuth = null, float? xrFeetBelowEye = null,
            bool? walk = null)
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
                frameRate ?? FrameRate,
                serverUrl ?? ServerUrl,
                token ?? Token,
                assetSync ?? AssetSync,
                xrHeight ?? XrHeight,
                xrLegacyScale ?? XrLegacyScale,
                xrDistance ?? XrDistance,
                xrAzimuth ?? XrAzimuth,
                xrFeetBelowEye ?? XrFeetBelowEye,
                walk ?? Walk);
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
        public MascotSettings WithServerUrl(string value) => Copy(serverUrl: value);
        public MascotSettings WithToken(string value) => Copy(token: value);
        public MascotSettings WithAssetSync(string value) => Copy(assetSync: value);
        public MascotSettings WithXrHeight(float value) => Copy(xrHeight: value);
        public MascotSettings WithXrLegacyScale(float value) => Copy(xrLegacyScale: value);
        public MascotSettings WithXrDistance(float value) => Copy(xrDistance: value);
        public MascotSettings WithXrAzimuth(float value) => Copy(xrAzimuth: value);
        public MascotSettings WithXrFeetBelowEye(float value) => Copy(xrFeetBelowEye: value);
        public MascotSettings WithWalk(bool value) => Copy(walk: value);

        /// <summary>
        /// 「すべての設定をリセット」用。<b>接続先とトークンだけは残す</b>。
        ///
        /// ★ 確認ダイアログが列挙する項目にも設定パネルの項目にも <see cref="ServerUrl"/> /
        ///   <see cref="Token"/> は無いので、単純に <see cref="Defaults"/> へ戻すと
        ///   消えたことに気付けないまま次の起動で既定の接続先に繋ぐ。
        /// </summary>
        public MascotSettings ResetKeepingConnection() => Defaults.WithServerUrl(ServerUrl).WithToken(Token);

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
                && FrameRate == other.FrameRate
                && string.Equals(ServerUrl, other.ServerUrl, StringComparison.Ordinal)
                && string.Equals(Token, other.Token, StringComparison.Ordinal)
                && string.Equals(AssetSync, other.AssetSync, StringComparison.Ordinal)
                && XrHeight.Equals(other.XrHeight)
                && XrLegacyScale.Equals(other.XrLegacyScale)
                && XrDistance.Equals(other.XrDistance)
                && XrAzimuth.Equals(other.XrAzimuth)
                && XrFeetBelowEye.Equals(other.XrFeetBelowEye)
                && Walk == other.Walk;
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
            hash = (hash * 397) ^ (ServerUrl != null ? ServerUrl.GetHashCode() : 0);
            hash = (hash * 397) ^ (Token != null ? Token.GetHashCode() : 0);
            hash = (hash * 397) ^ (AssetSync != null ? AssetSync.GetHashCode() : 0);
            hash = (hash * 397) ^ XrHeight.GetHashCode();
            hash = (hash * 397) ^ XrLegacyScale.GetHashCode();
            hash = (hash * 397) ^ XrDistance.GetHashCode();
            hash = (hash * 397) ^ XrAzimuth.GetHashCode();
            hash = (hash * 397) ^ XrFeetBelowEye.GetHashCode();
            hash = (hash * 397) ^ (Walk ? 1 : 0);
            return hash;
        }
    }
}
