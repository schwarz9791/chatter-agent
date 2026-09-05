using ChatterMascot.Protocol;
using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="EmotionMotionTrigger"/>。「鳴っている文の <c>order</c> が変わったフレームだけ、
    /// 感情モーションを発火すべきか判定する」状態機械。
    /// </summary>
    [TestFixture]
    public sealed class EmotionMotionTriggerTests
    {
        private static MotionParams Params(double cooldown = 5.0)
        {
            return new MotionParams(
                fadeSeconds: 0.5f, cooldownSeconds: cooldown, accentMinSeconds: 30.0, accentMaxSeconds: 60.0);
        }

        /// <summary>★ 毎フレーム発火し続けないための間引き。<c>order</c> が同じなら常に <c>null</c>。</summary>
        [Test]
        public void ReturnsNullWhileTheOrderDoesNotChange()
        {
            var trigger = new EmotionMotionTrigger(Params());

            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Happy, kind: SpeechKind.Assistant, now: 0.0, playingEmotion: false),
                Is.EqualTo(MotionCategory.Happy));

            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Happy, kind: SpeechKind.Assistant, now: 0.1, playingEmotion: false),
                Is.Null, "同じ order のまま");
            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Happy, kind: SpeechKind.Assistant, now: 0.2, playingEmotion: false),
                Is.Null);
        }

        [Test]
        public void ReturnsNullForNeutral()
        {
            var trigger = new EmotionMotionTrigger(Params());

            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Neutral, kind: SpeechKind.Assistant, now: 0.0, playingEmotion: false),
                Is.Null);
        }

        [Test]
        public void ReturnsNullForPrompt()
        {
            var trigger = new EmotionMotionTrigger(Params());

            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Surprised, kind: SpeechKind.Prompt, now: 0.0, playingEmotion: false),
                Is.Null);
        }

        [Test]
        public void ReturnsNullWhileAnEmotionMotionIsAlreadyPlaying()
        {
            var trigger = new EmotionMotionTrigger(Params());

            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Happy, kind: SpeechKind.Assistant, now: 0.0, playingEmotion: true),
                Is.Null);
        }

        [Test]
        public void ReturnsNullWithinTheCooldownAndFiresOnceItElapses()
        {
            var trigger = new EmotionMotionTrigger(Params(cooldown: 5.0));

            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Happy, kind: SpeechKind.Assistant, now: 0.0, playingEmotion: false),
                Is.EqualTo(MotionCategory.Happy));
            trigger.NotifyEnded(1.0);

            // 次の文（order が進んでいる）だが、まだクールダウン中
            Assert.That(
                trigger.Update(2, speaking: true, emotion: Emotion.Sad, kind: SpeechKind.Assistant, now: 3.0, playingEmotion: false),
                Is.Null, "クールダウン中");

            // クールダウンが明けた
            Assert.That(
                trigger.Update(3, speaking: true, emotion: Emotion.Sad, kind: SpeechKind.Assistant, now: 6.0, playingEmotion: false),
                Is.EqualTo(MotionCategory.Sad));
        }

        /// <summary>★ 連続する同じ emotion でも、order が進めば毎回候補になり得る。</summary>
        [Test]
        public void TheSameEmotionBecomesACandidateAgainOnceTheOrderAdvances()
        {
            var trigger = new EmotionMotionTrigger(Params(cooldown: 0.0));

            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Happy, kind: SpeechKind.Assistant, now: 0.0, playingEmotion: false),
                Is.EqualTo(MotionCategory.Happy));
            trigger.NotifyEnded(0.1);

            Assert.That(
                trigger.Update(2, speaking: true, emotion: Emotion.Happy, kind: SpeechKind.Assistant, now: 0.2, playingEmotion: false),
                Is.EqualTo(MotionCategory.Happy));
        }

        /// <summary>
        /// ★ <c>order == -1</c>（鳴っていない）は特別扱いされているわけではなく、
        ///   <c>speaking == false</c> で自然に落ちる。<c>order</c> が実在の値に変わり、
        ///   <c>speaking</c> も <c>true</c> になった回で初めて発火しうる。
        /// </summary>
        [Test]
        public void FiresWhenOrderMovesFromNotSpeakingToTheFirstSpokenSentence()
        {
            var trigger = new EmotionMotionTrigger(Params());

            Assert.That(
                trigger.Update(-1, speaking: false, emotion: Emotion.Neutral, kind: SpeechKind.Assistant, now: 0.0, playingEmotion: false),
                Is.Null);
            Assert.That(
                trigger.Update(1, speaking: true, emotion: Emotion.Surprised, kind: SpeechKind.Assistant, now: 0.1, playingEmotion: false),
                Is.EqualTo(MotionCategory.Surprised));
        }

        /// <summary>
        /// ★★ #70 レビュー #7。孤児（旧 epoch の音）と新しい文が重なって新しい方が先に鳴り終わると、
        ///   <c>SpeakingSet</c> が返す最大の <c>order</c> はまだ鳴っている孤児の小さい値へ戻る。
        ///   <c>order != _lastOrder</c>（変化があれば常に新規）ではこれを新しい文と誤認して
        ///   発火してしまうので、<c>order &gt; _lastOrder</c>（厳密に大きいときだけ新規）で
        ///   あることを固定する。クールダウンを 0 にして、判定が order の比較そのもので
        ///   決まっていることを確かめる。
        /// </summary>
        [Test]
        public void IgnoresOrderGoingBackwardsButFiresWhenItLaterAdvances()
        {
            var trigger = new EmotionMotionTrigger(Params(cooldown: 0.0));

            Assert.That(
                trigger.Update(7, speaking: true, emotion: Emotion.Happy, kind: SpeechKind.Assistant, now: 0.0, playingEmotion: false),
                Is.EqualTo(MotionCategory.Happy));
            trigger.NotifyEnded(0.1);

            // 孤児の重なりで order が戻ってきても、新しい文の開始とは見なさない
            Assert.That(
                trigger.Update(5, speaking: true, emotion: Emotion.Sad, kind: SpeechKind.Assistant, now: 0.2, playingEmotion: false),
                Is.Null, "order が戻っている");

            // 戻った order からでも、その後さらに進めば発火する
            Assert.That(
                trigger.Update(8, speaking: true, emotion: Emotion.Angry, kind: SpeechKind.Assistant, now: 0.3, playingEmotion: false),
                Is.EqualTo(MotionCategory.Angry));
        }
    }
}
