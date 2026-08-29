using ChatterMascot.Protocol;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 「いま鳴っているもの」から「顔が使う値」へ変換するときの記憶。<b>時計は引数で受け取る。</b>
    ///
    /// ★ <b>ラッチが要る理由。</b> <c>SpeakingView.TryRead</c> は再生中の item が無いとき
    ///   <c>kind = Assistant</c> / <c>emotion = Neutral</c> に<b>倒す契約</b>
    ///   （<c>SpeakingViewTests</c> の4本が固定している）。生の値をそのまま
    ///   <see cref="FacePolicy"/> へ渡すと、<b>喋り終わった瞬間に目標が Neutral になり、
    ///   <c>FaceParams.HoldSeconds</c> をいくら伸ばしても顔は即座に戻る</b>。
    ///
    /// ★ <b><c>Kind</c> も一緒にラッチすること。</b> <c>Emotion</c> だけラッチして <c>Kind</c> を
    ///   生のまま渡すと、猶予の途中で<b>片方だけ崩れる</b> ——
    ///   <c>FaceParams.PromptSurpriseWeight</c> を 0 から開けたとき、
    ///   emotion 由来の表情は猶予ぶん保たれるのに prompt の上乗せだけが
    ///   発話終了の次フレームで抜け、<b>目に見える段差</b>が入る。
    ///
    /// ★ <b>ただし prompt の<u>エッジ</u>は生の値で見ること。</b> ラッチ済みの <see cref="Kind"/> は
    ///   猶予の間も（次の発話まで）<c>Prompt</c> のまま残るので、そちらでエッジを取ると
    ///   <b>2回目以降の prompt でエッジが立たず、瞬きが一度も入らなくなる</b>。
    ///
    /// ★ <b>ここを <c>VrmCharacter</c> のフィールドとして書かないこと</b>（#57 のレビュー指摘）。
    ///   <c>ChatterMascot.Tests.asmdef</c> は <c>ChatterMascot.Runtime</c> しか参照しないので、
    ///   <c>MonoBehaviour</c> に書いた時点で<b>テストが1行も当たらなくなる</b> ——
    ///   しかもここは「これが無いと猶予が1行も効かない」と分かっている場所。
    /// </summary>
    public sealed class FaceLatch
    {
        /// <summary>発話中に読んだ最後の emotion。<see cref="FacePolicy"/> へはこれを渡す。</summary>
        public Emotion Emotion { get; private set; } = Emotion.Neutral;

        /// <summary>発話中に読んだ最後の kind。<see cref="FacePolicy"/> へはこれを渡す。</summary>
        public SpeechKind Kind { get; private set; } = SpeechKind.Assistant;

        /// <summary>
        /// 直近で発話が止まった時刻。
        /// ★ 一度も喋っていない間は <c>-∞</c>。差が <c>+∞</c> になって猶予の外に確定する。
        /// </summary>
        public double SpeechEndedAt { get; private set; } = double.NegativeInfinity;

        private bool _wasSpeaking;
        private bool _wasPrompt;

        /// <summary>
        /// 1フレーム分進める。
        /// </summary>
        /// <returns>
        /// <c>kind</c> が <c>prompt</c> に<b>変わったエッジ</b>なら <c>true</c>
        /// （呼び出し側は <see cref="BlinkTimer.Request"/> を1回だけ呼ぶ）。
        /// ★ 毎フレーム <c>true</c> を返さないこと —— 瞬きっぱなしになる。
        /// </returns>
        public bool Update(bool speaking, Emotion emotion, SpeechKind kind, double now)
        {
            if (speaking)
            {
                Emotion = emotion;
                Kind = kind;
            }
            else if (_wasSpeaking)
            {
                SpeechEndedAt = now;
            }
            _wasSpeaking = speaking;

            // ★ 生の kind で見る（ラッチ済みの Kind ではない）。理由はクラスの doc を参照
            var isPrompt = speaking && kind == SpeechKind.Prompt;
            var edge = isPrompt && !_wasPrompt;
            _wasPrompt = isPrompt;

            return edge;
        }
    }
}
