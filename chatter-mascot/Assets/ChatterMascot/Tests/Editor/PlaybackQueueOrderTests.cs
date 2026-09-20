using ChatterMascot.Playback;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class PlaybackQueueOrderTests : PlaybackQueueTestBase
    {
        /// <summary>受信すると取得が始まり、取得が終われば head から再生される</summary>
        [Test]
        public void FetchesOnReceiveAndPlaysFromHead()
        {
            var state = Start();
            var queued = Run(state, PlaybackEvent.Received(Record(1)));

            var fetches = Only(queued, PlaybackCommandKind.FetchAudio);
            Assert.That(fetches.Count, Is.EqualTo(1));
            Assert.That(fetches[0].Seq, Is.EqualTo(1L));
            Assert.That(fetches[0].Path, Is.EqualTo(AudioPathOf(E1, 1)));
            Assert.That(fetches[0].Epoch, Is.EqualTo(0));

            var playing = Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));
            var plays = Only(playing, PlaybackCommandKind.Play);
            Assert.That(plays.Count, Is.EqualTo(1));
            Assert.That(plays[0].Seq, Is.EqualTo(1L));
            Assert.That(plays[0].Audio, Is.EqualTo(Clip(1)));
        }

        /// <summary>先読みの件数だけ取得を先行させる（再生中の1件を含む）</summary>
        [Test]
        public void PrefetchesLookaheadPlusHead()
        {
            var state = Start(o => o.Lookahead = 2);
            var commands = Run(state, ReceivedAll(1, 2, 3, 4, 5));
            // lookahead=2 なら head を含めて3件まで
            Assert.That(SeqsOf(commands, PlaybackCommandKind.FetchAudio), Is.EqualTo(new long[] { 1, 2, 3 }));
        }

        /// <summary>lookahead=0 なら完全直列になる</summary>
        [Test]
        public void SerialWhenLookaheadIsZero()
        {
            var state = Start(o => o.Lookahead = 0);
            var commands = Run(state, ReceivedAll(1, 2, 3));
            Assert.That(SeqsOf(commands, PlaybackCommandKind.FetchAudio), Is.EqualTo(new long[] { 1 }));
        }

        /// <summary>★ 後ろの取得が先に終わっても head を追い越さない</summary>
        [Test]
        public void DoesNotOvertakeHead()
        {
            var state = Start();
            Run(state, ReceivedAll(1, 2));

            // seq 2 だけ取得完了
            var commands = Run(state, PlaybackEvent.AudioReady(0, 2, Clip(2)));
            Assert.That(Only(commands, PlaybackCommandKind.Play), Is.Empty);

            // seq 1 が揃って初めて 1 から鳴る
            var after = Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));
            Assert.That(SeqsOf(after, PlaybackCommandKind.Play), Is.EqualTo(new long[] { 1 }));
        }

        /// <summary>
        /// ★ 消費するたびに窓を再評価する（lookahead+1 文目以降が無音にならない）。
        /// ここが抜けると 4 文目以降が永久に喋られない。
        /// </summary>
        [Test]
        public void ReevaluatesWindowOnConsume()
        {
            var state = Start(o => o.Lookahead = 2);
            Run(state, ReceivedAll(1, 2, 3, 4, 5));

            var commands = Speak(state, 1);
            Assert.That(SeqsOf(commands, PlaybackCommandKind.FetchAudio), Is.EqualTo(new long[] { 4 }));

            var next = Speak(state, 2);
            Assert.That(SeqsOf(next, PlaybackCommandKind.FetchAudio), Is.EqualTo(new long[] { 5 }));
        }

        /// <summary>
        /// ★ seq に飛びがあっても先読みが止まらない（数値の窓ではなく位置の窓）。
        /// CLI の trim やサーバー再起動で seq は飛ぶ。数値窓（head + lookahead）だと
        /// 対象ゼロになり、音は出るのに先読みだけが恒久的に効かなくなる。
        /// </summary>
        [Test]
        public void WindowIsPositionalNotNumeric()
        {
            var state = Start(o => o.Lookahead = 2);
            var commands = Run(state, ReceivedAll(10, 51, 52, 53));
            Assert.That(SeqsOf(commands, PlaybackCommandKind.FetchAudio), Is.EqualTo(new long[] { 10, 51, 52 }));
        }

        /// <summary>受信順が seq 順でなくても seq 昇順に再生する（接続直後の追いつき）</summary>
        [Test]
        public void PlaysInSeqOrderRegardlessOfArrival()
        {
            var state = Start();
            Run(state, ReceivedAll(3, 1, 2));
            var commands = Run(state, new[]
            {
                PlaybackEvent.AudioReady(0, 3, Clip(3)),
                PlaybackEvent.AudioReady(0, 2, Clip(2)),
                PlaybackEvent.AudioReady(0, 1, Clip(1)),
            });
            Assert.That(SeqsOf(commands, PlaybackCommandKind.Play), Is.EqualTo(new long[] { 1 }));
        }

    }
}
