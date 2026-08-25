using UnityEngine;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// プラットフォームに合う <see cref="ISpeechPlayer"/> を選ぶ。
    ///
    /// ★ <b>分かれ目は「無音のときにオーディオ出力デバイスを手放せるか」</b>だけ。
    ///
    /// | | 実装 | 手放し方 |
    /// |---|---|---|
    /// | macOS | <see cref="AfplaySpeechPlayer"/> | 1発話 = 1プロセス。鳴り終われば OS が解放する |
    /// | Android / iOS | <see cref="AudioClipPlayer"/> | <c>AudioSettings.Mobile.StopAudioOutput()</c> |
    /// | その他 | <see cref="AudioClipPlayer"/> | <b>手放せない</b>（Windows / Linux は未対応） |
    ///
    /// ★ <b>macOS では <c>Disable Unity Audio</c> が ON でないと意味が無い。</b>
    ///   外部プロセスで鳴らしても、Unity 内蔵オーディオが有効なままだと Unity 側が
    ///   デバイスを掴み続ける（実測）。ビルド時の切り替えは <c>BuildScript.BuildMacOS</c>。
    ///
    /// ★ <b>Editor（macOS）でも <see cref="AfplaySpeechPlayer"/> を使う。</b> 本番と同じ経路を
    ///   Play Mode で確かめられるようにするため。ただし Android 向けの開発を macOS の Editor で
    ///   するときは、<b>Editor と実機で再生の実体が変わる</b>ことに注意。
    /// </summary>
    public static class SpeechPlayerFactory
    {
        /// <param name="template">
        /// Unity 内蔵オーディオで鳴らすときの <see cref="AudioSource"/>。
        /// 外部プロセスで鳴らす実装では使わない。
        /// </param>
        public static ISpeechPlayer Create(AudioSource template)
        {
#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            return new AfplaySpeechPlayer();
#else
            return new AudioClipPlayer(template);
#endif
        }
    }
}
