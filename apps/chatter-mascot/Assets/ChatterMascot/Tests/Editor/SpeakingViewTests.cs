using ChatterMascot.Playback;
using ChatterMascot.Protocol;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="SpeakingView"/>。<c>PlaybackState</c> から「いま何を喋っているか」を読むだけの
    /// 暫定品（#58 の <c>SpeakingSet</c> が入ったら用済みになる）。
    ///
    /// ★ <c>state.Orphans</c> は <c>Record</c> を持たないので、孤児だけが鳴っている間は
    ///   <c>false</c> を返す（既知の穴。ここでは直さない）。
    /// </summary>
    [TestFixture]
    public sealed class SpeakingViewTests
    {
        private static PlaybackState State()
        {
            return new PlaybackState(null);
        }

        private static QueueItem Item(ItemStatus status, SpeechKind kind = SpeechKind.Assistant, Emotion emotion = Emotion.Neutral)
        {
            return new QueueItem
            {
                Status = status,
                Record = new SpeechFrame { Kind = kind, Emotion = emotion },
            };
        }

        [Test]
        public void ReturnsFalseWhenNothingIsPlaying()
        {
            var state = State();
            state.Items[1] = Item(ItemStatus.Pending);
            state.Items[2] = Item(ItemStatus.Ready);
            state.Items[3] = Item(ItemStatus.Done);

            var found = SpeakingView.TryRead(state, out var kind, out var emotion);

            Assert.That(found, Is.False);
            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
        }

        [Test]
        public void ReturnsKindAndEmotionOfThePlayingItem()
        {
            var state = State();
            state.Items[1] = Item(ItemStatus.Done);
            state.Items[2] = Item(ItemStatus.Playing, SpeechKind.Prompt, Emotion.Surprised);

            var found = SpeakingView.TryRead(state, out var kind, out var emotion);

            Assert.That(found, Is.True);
            Assert.That(kind, Is.EqualTo(SpeechKind.Prompt));
            Assert.That(emotion, Is.EqualTo(Emotion.Surprised));
        }

        /// <summary>
        /// 再生されるのは head だけなので <c>Playing</c> は高々1件のはずだが、
        /// 複数あった場合は防御的に seq 最小を採る。
        /// </summary>
        [Test]
        public void PicksTheSmallestSeqWhenMultipleAreMarkedPlaying()
        {
            var state = State();
            state.Items[5] = Item(ItemStatus.Playing, SpeechKind.Assistant, Emotion.Angry);
            state.Items[2] = Item(ItemStatus.Playing, SpeechKind.Prompt, Emotion.Happy);
            state.Items[9] = Item(ItemStatus.Playing, SpeechKind.Assistant, Emotion.Sad);

            var found = SpeakingView.TryRead(state, out var kind, out var emotion);

            Assert.That(found, Is.True);
            Assert.That(kind, Is.EqualTo(SpeechKind.Prompt));
            Assert.That(emotion, Is.EqualTo(Emotion.Happy));
        }

        /// <summary>
        /// 孤児（<c>Orphans</c>）だけが鳴っていても <c>false</c>。<c>Orphans</c> の値は
        /// 音声ハンドルだけで <c>Record</c> を持たないので、原理的に読めない。
        /// </summary>
        [Test]
        public void ReturnsFalseWhenOnlyOrphansArePlaying()
        {
            var state = State();
            state.Orphans["0:1"] = new object();

            var found = SpeakingView.TryRead(state, out var kind, out var emotion);

            Assert.That(found, Is.False);
            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
        }

        [Test]
        public void DoesNotThrowWhenStateIsNull()
        {
            var found = SpeakingView.TryRead(null, out var kind, out var emotion);

            Assert.That(found, Is.False);
            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
        }

        [Test]
        public void DoesNotThrowWhenThePlayingItemHasNoRecord()
        {
            var state = State();
            state.Items[1] = new QueueItem { Status = ItemStatus.Playing, Record = null };

            var found = SpeakingView.TryRead(state, out var kind, out var emotion);

            Assert.That(found, Is.False);
            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
        }
    }
}
