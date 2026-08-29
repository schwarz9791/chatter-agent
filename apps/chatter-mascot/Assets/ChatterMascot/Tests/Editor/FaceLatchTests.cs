using ChatterMascot.Protocol;
using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="FaceLatch"/>。「これが無いと <c>FaceParams.HoldSeconds</c> が1行も効かない」
    /// と分かっている場所なので、<c>VrmCharacter</c>（テストから見えない <c>ChatterMascot.Vrm</c>）
    /// ではなく <c>ChatterMascot.Runtime</c> に置いてここで固定する。
    /// </summary>
    [TestFixture]
    public sealed class FaceLatchTests
    {
        private static bool Speak(FaceLatch latch, Emotion emotion, double now, SpeechKind kind = SpeechKind.Assistant)
        {
            return latch.Update(speaking: true, emotion: emotion, kind: kind, now: now);
        }

        private static bool Silent(FaceLatch latch, double now)
        {
            // ★ 喋っていないとき SpeakingSet が返すのは既定値（Assistant / Neutral）。
            //   ラッチの意味はここで壊れないことにある
            return latch.Update(speaking: false, emotion: Emotion.Neutral, kind: SpeechKind.Assistant, now: now);
        }

        [Test]
        public void StartsNeutralAndOutsideTheGracePeriod()
        {
            var latch = new FaceLatch();

            Assert.That(latch.Emotion, Is.EqualTo(Emotion.Neutral));
            Assert.That(latch.Kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(latch.SpeechEndedAt, Is.EqualTo(double.NegativeInfinity));
        }

        /// <summary>
        /// ★ <b>これが無いと猶予が1行も効かない。</b> <c>SpeakingSet.TryGetFace</c> は
        /// 再生中の item が無いとき <c>Neutral</c> に倒すので、素通しすると
        /// 喋り終わった瞬間に目標が Neutral になる。
        /// </summary>
        [Test]
        public void KeepsTheLastSpokenEmotionAfterSpeechStops()
        {
            var latch = new FaceLatch();

            Speak(latch, Emotion.Happy, 10.0);
            Silent(latch, 10.1);
            Silent(latch, 12.0);

            Assert.That(latch.Emotion, Is.EqualTo(Emotion.Happy));
        }

        /// <summary>
        /// ★ <c>Kind</c> もラッチすること。<c>Emotion</c> だけだと、
        /// <c>promptSurpriseWeight</c> を開けたときに猶予の途中で上乗せだけが抜けて段差になる。
        /// </summary>
        [Test]
        public void KeepsTheLastSpokenKindAfterSpeechStops()
        {
            var latch = new FaceLatch();

            Speak(latch, Emotion.Surprised, 10.0, SpeechKind.Prompt);
            Silent(latch, 10.1);

            Assert.That(latch.Kind, Is.EqualTo(SpeechKind.Prompt));
            Assert.That(latch.Emotion, Is.EqualTo(Emotion.Surprised));
        }

        [Test]
        public void RecordsWhenSpeechStoppedOnTheFallingEdgeOnly()
        {
            var latch = new FaceLatch();

            Speak(latch, Emotion.Happy, 10.0);
            Silent(latch, 10.5);
            Assert.That(latch.SpeechEndedAt, Is.EqualTo(10.5));

            // ★ 立ち下がりの1回だけ。黙り続けている間に更新すると猶予が終わらない
            Silent(latch, 99.0);
            Assert.That(latch.SpeechEndedAt, Is.EqualTo(10.5));
        }

        [Test]
        public void RecordsTheStopAgainAfterSpeakingResumes()
        {
            var latch = new FaceLatch();

            Speak(latch, Emotion.Happy, 10.0);
            Silent(latch, 10.5);
            Speak(latch, Emotion.Sad, 11.0);
            Silent(latch, 12.5);

            Assert.That(latch.SpeechEndedAt, Is.EqualTo(12.5));
            Assert.That(latch.Emotion, Is.EqualTo(Emotion.Sad));
        }

        [Test]
        public void ReportsThePromptEdgeExactlyOnce()
        {
            var latch = new FaceLatch();

            Assert.That(Speak(latch, Emotion.Neutral, 1.0), Is.False, "assistant では立たない");
            Assert.That(Speak(latch, Emotion.Surprised, 2.0, SpeechKind.Prompt), Is.True, "エッジ");
            Assert.That(Speak(latch, Emotion.Surprised, 2.1, SpeechKind.Prompt), Is.False, "同じ prompt の続き");
            Assert.That(Speak(latch, Emotion.Surprised, 2.2, SpeechKind.Prompt), Is.False);
        }

        /// <summary>
        /// ★ <b>ラッチ済みの <c>Kind</c> でエッジを取ってはいけない。</b> あちらは猶予の間も
        /// <c>Prompt</c> のまま残るので、2回目の prompt でエッジが立たず瞬きが入らなくなる。
        /// </summary>
        [Test]
        public void ReportsThePromptEdgeAgainForTheNextPrompt()
        {
            var latch = new FaceLatch();

            Assert.That(Speak(latch, Emotion.Surprised, 1.0, SpeechKind.Prompt), Is.True);
            Silent(latch, 2.0);
            Assert.That(latch.Kind, Is.EqualTo(SpeechKind.Prompt), "ラッチは Prompt のまま");

            // 間に assistant を挟まずに次の prompt が来ても、エッジは立つ
            Assert.That(Speak(latch, Emotion.Surprised, 3.0, SpeechKind.Prompt), Is.True);
        }

        [Test]
        public void DoesNotReportAnEdgeWhileSilent()
        {
            var latch = new FaceLatch();

            Assert.That(Silent(latch, 1.0), Is.False);
            Assert.That(latch.Update(speaking: false, emotion: Emotion.Surprised, kind: SpeechKind.Prompt, now: 2.0),
                Is.False, "鳴っていないなら prompt でもエッジにしない");
        }
    }
}
