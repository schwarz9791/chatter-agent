using System.Text.RegularExpressions;

namespace ChatterMascot.Protocol
{
    /// <summary>
    /// 採番の世代。<c>seq</c> は<b>この中でしか一意でない</b>。
    ///
    /// ランタイムルート（または <c>speech.state.json</c> と <c>speech.jsonl</c> の両方）が消えると
    /// CLI の採番は 1 に戻る。<c>seq</c> だけを覚えている受信側は、そこで「もう喋った」と誤判定して
    /// <b>何百文でも一切喋らなくなる</b>（エラーも出ない）。
    ///
    /// ★ 比較は<b>等値だけ</b>。順序は無い（値から「新しい epoch」を判定できない）。
    /// ★ <b>URL の一部になる</b>（<c>/audio/&lt;epoch&gt;-&lt;seq&gt;.wav</c>）。charset から
    ///   外れる値を通さないこと。
    /// </summary>
    public static class SpeechEpoch
    {
        // ★ core/src/core/types.ts の EPOCH_PATTERN と揃えること。ここを緩めると
        //   `/audio/../../etc/passwd` のような入力が通る余地ができる。
        //
        // ★ RegexOptions.Compiled を使わないこと。IL2CPP は実行時のコード生成ができない。
        private static readonly Regex Pattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.None);

        public static bool IsValid(string value)
        {
            return !string.IsNullOrEmpty(value) && Pattern.IsMatch(value);
        }
    }

    /// <summary>
    /// 合成済み音声の URL パス（<c>/audio/&lt;epoch&gt;-&lt;12桁ゼロ埋めした seq&gt;.wav</c>）。
    ///
    /// ★ <b>絶対 URL を通さないこと。</b> 任意の URL を受け入れると、サーバーが
    ///   クライアントを任意の外部ホストへ向かわせられる。この形だけを通す。
    /// </summary>
    public static class AudioPath
    {
        private static readonly Regex Pattern =
            new Regex(@"^/audio/[A-Za-z0-9][A-Za-z0-9._-]{0,63}-\d{12}\.wav$", RegexOptions.None);

        public static bool IsValid(string value)
        {
            return !string.IsNullOrEmpty(value) && Pattern.IsMatch(value);
        }
    }
}
