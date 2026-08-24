using ChatterMascot.Playback;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class PlaybackQueueFailureTests : PlaybackQueueTestBase
    {
        /// <summary>取得の失敗は1回だけリトライする</summary>
        [Test]
        public void RetriesFetchOnce()
        {
            var state = Start(o => o.SynthesisAttempts = 2);
            Run(state, PlaybackEvent.Received(Record(1)));

            var retried = Run(state, PlaybackEvent.AudioFailed(0, 1, "500"));
            Assert.That(SeqsOf(retried, PlaybackCommandKind.FetchAudio), Is.EqualTo(new long[] { 1 }));
            Assert.That(Only(retried, PlaybackCommandKind.Ack), Is.Empty);

            var given = Run(state, PlaybackEvent.AudioFailed(0, 1, "500"));
            Assert.That(Only(given, PlaybackCommandKind.FetchAudio), Is.Empty);
            var acks = Only(given, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
            Assert.That(Only(given, PlaybackCommandKind.Warn).Count, Is.EqualTo(1));
        }

        /// <summary>再生の失敗はリトライしない（途中まで鳴った文が頭から鳴り直す）</summary>
        [Test]
        public void DoesNotRetryPlayback()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));
            Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));

            var commands = Run(state, PlaybackEvent.PlaybackFailed(0, 1, "exit 1"));
            Assert.That(Only(commands, PlaybackCommandKind.Play), Is.Empty);
            var acks = Only(commands, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
        }

        /// <summary>失敗した文の音声も解放する</summary>
        [Test]
        public void DiscardsAudioOfFailedItem()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));
            Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));
            var commands = Run(state, PlaybackEvent.PlaybackFailed(0, 1, "timeout"));
            var discards = Only(commands, PlaybackCommandKind.DiscardAudio);
            Assert.That(discards.Count, Is.EqualTo(1));
            Assert.That(discards[0].Seq, Is.EqualTo(1L));
            Assert.That(discards[0].Audio, Is.EqualTo(Clip(1)));
        }

        /// <summary>
        /// 音声が無いフレーム（audio: null）は取りに行かず、そのまま ack する。
        /// 約物だけの断片と ttsEnabled: false が、サーバー側でどちらも audio: null になる。
        /// </summary>
        [Test]
        public void FrameWithoutAudioIsAckedImmediately()
        {
            var state = Start();
            var commands = Run(state, PlaybackEvent.Received(Record(1, noAudio: true, text: "！")));
            Assert.That(Only(commands, PlaybackCommandKind.FetchAudio), Is.Empty);
            var acks = Only(commands, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
        }

        /// <summary>捨てた後に取得が返ってきたら音声だけ解放する</summary>
        [Test]
        public void LateResultAfterGivingUpIsDiscarded()
        {
            var state = Start(o => o.SynthesisAttempts = 1);
            Run(state, PlaybackEvent.Received(Record(1)));
            Run(state, PlaybackEvent.AudioFailed(0, 1, "timeout"));

            var late = Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));
            var discards = Only(late, PlaybackCommandKind.DiscardAudio);
            Assert.That(discards.Count, Is.EqualTo(1));
            Assert.That(discards[0].Audio, Is.EqualTo(Clip(1)));
            Assert.That(Only(late, PlaybackCommandKind.Play), Is.Empty);
        }
    }
}
