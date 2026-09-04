using ChatterMascot.Protocol;

namespace ChatterMascot.Vrm
{
    /// <summary>
    /// 鳴り始めた文の <c>emotion</c> から、感情モーションを発火すべきか判定する。
    /// <b>純粋な状態機械。時計は引数で受け取る。</b>
    ///
    /// ★ <c>ChatterMascot.Tests.asmdef</c> は <c>ChatterMascot.Runtime</c> しか参照しないので、
    ///   判断はここに置く（<c>VrmCharacter</c> に書くとテストが1行も当たらない）。
    ///
    /// ★★ <b>「文の開始」は <paramref name="order"/> の変化でしか取れない。</b>
    ///   <c>Speaking</c> の立ち上がりでは取れない——先読みが効いていると、文の切れ目で
    ///   <c>Speaking</c> は <c>false</c> に落ちない（<c>AfplaySpeechPlayer.PlayAsync → End →
    ///   Dispatch(Played) → 次の Play → BeginSpeaking</c> が同じ継続で同期に走るため）。
    ///   <c>order</c> は <c>SpeakingSet.Entry.Order</c>（再生を <c>Begin</c> した順の通し番号）。
    /// </summary>
    public sealed class EmotionMotionTrigger
    {
        private readonly MotionParams _params;

        private long _lastOrder = -1;
        private double _lastEndedAt = double.NegativeInfinity;

        public EmotionMotionTrigger(MotionParams p)
        {
            _params = p;
        }

        /// <summary>
        /// 発火すべきカテゴリ。発火しないなら <c>null</c>。
        ///
        /// ★ <b><paramref name="order"/> が前回の呼び出しと同じなら常に <c>null</c></b>
        ///   （毎フレーム発火し続けないための間引き。変わったフレームだけ以下を評価する）。
        /// ★ <paramref name="order"/><c>== -1</c>（鳴っていない）は、「前回と違っても」評価しない——
        ///   といっても特別扱いしているわけではなく、<paramref name="speaking"/> が
        ///   <c>false</c> になるので下の条件で自然に落ちる。
        /// </summary>
        /// <param name="order">いま鳴っている文の <c>SpeakingSet</c> の <c>Order</c>。鳴っていなければ -1</param>
        /// <param name="speaking">いま何か鳴っているか</param>
        /// <param name="emotion">鳴っている文の emotion</param>
        /// <param name="kind">鳴っている文の kind。<c>Prompt</c> では発火しない</param>
        /// <param name="now">現在時刻</param>
        /// <param name="playingEmotion">いま感情モーションを再生中か（再生中には割り込まない）</param>
        public MotionCategory? Update(
            long order, bool speaking, Emotion emotion, SpeechKind kind, double now, bool playingEmotion)
        {
            var isNewOrder = order != _lastOrder;
            _lastOrder = order;
            if (!isNewOrder) return null;

            if (!speaking) return null;
            if (kind == SpeechKind.Prompt) return null;
            if (playingEmotion) return null;
            if (now - _lastEndedAt < _params.CooldownSeconds) return null;

            // Neutral は MotionCategories.FromEmotion が null を返す＝発火しない
            return MotionCategories.FromEmotion(emotion);
        }

        /// <summary>
        /// 感情モーションが終わった。クールダウンの起点をここに更新する。
        /// ★ <c>VrmMotionPlayer.Tick</c> が <c>Ended</c>（かつ <c>Kind == Emotion</c>）を検出した
        ///   フレームで呼ぶ。
        /// </summary>
        public void NotifyEnded(double now)
        {
            _lastEndedAt = now;
        }
    }
}
