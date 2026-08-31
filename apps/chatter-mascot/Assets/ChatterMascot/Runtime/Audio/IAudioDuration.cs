namespace ChatterMascot.Audio
{
    /// <summary>
    /// 不透明ハンドル（<see cref="ISpeechPlayer.Prepare"/> が作る値）のうち、
    /// <b>再生時間を知っているもの</b>が実装する。
    ///
    /// ★ <b><see cref="ISpeechPlayer"/> を変更しない</b>ための型
    ///   （<see cref="ILipSyncSource"/> とまったく同じ流儀）。状態機械（<c>PlaybackQueue</c>）から
    ///   見れば依然 <c>object</c> の不透明ハンドルのままで、長さを要る人だけが
    ///   <c>audio as IAudioDuration</c> で読む。
    ///
    /// ★ <b>使い道は「無音で待つ」1つだけ。</b> ミュート中に音を出さずに実時間を消費するために要る
    ///   （→ <see cref="MutedSpeechPlayer"/>）。待たずに即返すと、溜まっていた発話が
    ///   数百 ms で全部消化されて<b>表情が高速で切り替わる</b>。
    /// </summary>
    public interface IAudioDuration
    {
        /// <summary>再生時間（ミリ秒）。<b>0 は「長さ 0」ではなく「不明」</b></summary>
        int DurationMs { get; }
    }
}
