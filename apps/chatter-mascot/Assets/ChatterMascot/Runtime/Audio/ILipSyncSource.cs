namespace ChatterMascot.Audio
{
    /// <summary>
    /// 不透明ハンドル（<see cref="ISpeechPlayer.Prepare"/> が作る値）のうち、
    /// <b>口を動かす材料を持っているもの</b>が実装する。
    ///
    /// ★ <b><see cref="ISpeechPlayer"/> は変更しない。</b> 状態機械（<c>PlaybackQueue</c>）から
    ///   見れば依然 <c>object</c> の不透明ハンドルのままで、<c>MascotRunner</c> だけが
    ///   <c>audio as ILipSyncSource</c> で読む。実装を1つ増やしても
    ///   <c>PlaybackQueue</c> と EditMode テストのコマンド列比較に一切触れない。
    ///
    /// ★ <b>なぜ <c>AudioSource.GetOutputData</c> ではないか。</b> macOS の再生の実体は
    ///   <c>AfplaySpeechPlayer</c>（1発話 = 1つの <c>afplay</c> 子プロセス）で、
    ///   <b>音は子プロセスの中にあり <c>GetOutputData</c> に相当するものが存在しない</b>。
    ///   <c>MascotRunner._player</c> も <see cref="ISpeechPlayer"/> 型なので、
    ///   <c>AudioClipPlayer</c> が内部で持つ <c>AudioSource</c> はインターフェース越しに届かない。
    ///   <b><c>Prepare</c> の時点で WAV から作っておけば、3つの実装すべてで同じコードが使える。</b>
    ///
    /// ★ <b>プロパティであってフィールドではない。</b> C# はインターフェースのメンバーを
    ///   フィールドで実装できないので、ハンドル側は自動プロパティになる
    ///   （既存の <c>Path</c> / <c>Clip</c> / <c>DurationMs</c> は public フィールドなので
    ///   書き方が揃わないが、揃えるために明示的実装を挟むほうが読みにくい）。
    /// </summary>
    public interface ILipSyncSource
    {
        /// <summary>
        /// <see cref="EnvelopeFrameMs"/> ごとの RMS。<b><c>null</c> なら口を動かさない。</b>
        ///
        /// ★ <b>生の RMS を入れること（ゲインを焼き込まない）。</b> 焼くと Inspector から
        ///   調整できなくなるうえ、後段でもう一度掛かる二重適用の事故が起きる。
        /// </summary>
        float[] Envelope { get; }

        /// <summary>
        /// 1要素あたりの長さ（ミリ秒）。→ <see cref="LipSyncEnvelope.DefaultFrameMs"/>
        ///
        /// ★ <b>これは公称値。</b> 実フレームは
        /// <c>floor(SampleRate * frameMs / 1000)</c> サンプルなので、
        /// <c>SampleRate * frameMs % 1000 == 0</c> のときだけ厳密に一致する。
        /// 割り切れないレート（11025Hz など）では実フレームが公称より<b>短く</b>なり、
        /// エンベロープが公称より速く進む＝<b>口は音より遅れる側にずれる</b>
        /// （<c>lipSyncOffsetMs</c> の doc が好ましいとする向き。11025Hz で 0.23%）。
        /// ★ 実運用のレート（16000 / 22050 / 24000 / 44100 / 48000）はすべて割り切れる。
        /// <c>LipSyncEnvelopeTests.EveryRateWeActuallyMeetDividesEvenly</c> がそれを固定している。
        /// </summary>
        int EnvelopeFrameMs { get; }
    }
}
