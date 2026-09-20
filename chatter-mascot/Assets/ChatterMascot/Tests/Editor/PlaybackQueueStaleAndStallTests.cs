using ChatterMascot.Playback;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class PlaybackQueueStaleTests : PlaybackQueueTestBase
    {
        /// <summary>MaxAgeMs が 0 なら何も飛ばさない</summary>
        [Test]
        public void NoStaleCheckWhenDisabled()
        {
            var state = Start(o => o.MaxAgeMs = 0);
            var commands = Run(state, PlaybackEvent.Received(Record(1)), T0 + 600000);
            Assert.That(Only(commands, PlaybackCommandKind.FetchAudio).Count, Is.EqualTo(1));
        }

        /// <summary>MaxAgeMs を超えた発話は音を出さずに ack する</summary>
        [Test]
        public void StaleFrameIsAckedWithoutAudio()
        {
            var state = Start(o => o.MaxAgeMs = 60000);
            var commands = Run(state, PlaybackEvent.Received(Record(1)), T0 + 120000);
            Assert.That(Only(commands, PlaybackCommandKind.FetchAudio), Is.Empty);
            var acks = Only(commands, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
        }

        /// <summary>ts が読めないものは古さで捨てない</summary>
        [Test]
        public void UnparsableTsIsNotStale()
        {
            var state = Start(o => o.MaxAgeMs = 60000);
            var commands = Run(state, PlaybackEvent.Received(Record(1, ts: "いつか")), T0 + 120000);
            Assert.That(Only(commands, PlaybackCommandKind.FetchAudio).Count, Is.EqualTo(1));
        }

        /// <summary>再生中のものは古くなっても止めない</summary>
        [Test]
        public void PlayingItemIsNotCutOff()
        {
            var state = Start(o => o.MaxAgeMs = 60000);
            Run(state, PlaybackEvent.Received(Record(1)));
            Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));
            var commands = Run(state, PlaybackEvent.Tick(), T0 + 120000);
            Assert.That(Only(commands, PlaybackCommandKind.Ack), Is.Empty);
            Assert.That(state.Items[1].Status, Is.EqualTo(ItemStatus.Playing));
        }
    }

    [TestFixture]
    public sealed class PlaybackQueueStallTests : PlaybackQueueTestBase
    {
        /// <summary>head が動かないまま時間が経つと警告し、★ StallWarnMs ごとに出し直す</summary>
        [Test]
        public void WarnsOnStallAndRearms()
        {
            var state = Start(o => o.StallWarnMs = 60000);
            Run(state, PlaybackEvent.Received(Record(1)));

            Assert.That(Only(Run(state, PlaybackEvent.Tick(), T0 + 30000), PlaybackCommandKind.Warn), Is.Empty);

            var warned = Only(Run(state, PlaybackEvent.Tick(), T0 + 61000), PlaybackCommandKind.Warn);
            Assert.That(warned.Count, Is.EqualTo(1));
            Assert.That(warned[0].Message, Does.Contain("seq=1"));

            // 間隔の中では繰り返さない
            Assert.That(Only(Run(state, PlaybackEvent.Tick(), T0 + 90000), PlaybackCommandKind.Warn), Is.Empty);

            // ★ 恒久的に詰まると head は永遠に変わらない。HeadSeq の変化でしか再武装しない形だと
            //   生涯1行しか出ず、「無音なのにログが2行だけ」になる
            Assert.That(Only(Run(state, PlaybackEvent.Tick(), T0 + 130000), PlaybackCommandKind.Warn).Count,
                Is.EqualTo(1));
        }

        /// <summary>head が進めば警告しない</summary>
        [Test]
        public void NoWarnWhenHeadAdvances()
        {
            var state = Start(o => o.StallWarnMs = 60000);
            Run(state, ReceivedAll(1, 2));
            Speak(state, 1, T0 + 30000);
            Assert.That(Only(Run(state, PlaybackEvent.Tick(), T0 + 61000), PlaybackCommandKind.Warn), Is.Empty);
        }

        /// <summary>キューが空なら警告しない</summary>
        [Test]
        public void NoWarnWhenQueueIsEmpty()
        {
            var state = Start(o => o.StallWarnMs = 60000);
            Assert.That(Only(Run(state, PlaybackEvent.Tick(), T0 + 600000), PlaybackCommandKind.Warn), Is.Empty);
        }
    }

    [TestFixture]
    public sealed class PlaybackQueueDisconnectTests : PlaybackQueueTestBase
    {
        /// <summary>★ 切断で items を捨てない（再送で取得をやり直さない）</summary>
        [Test]
        public void DisconnectKeepsItems()
        {
            var state = Start(o => o.Lookahead = 3);
            Run(state, ReceivedAll(1, 2, 3));
            Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));

            Run(state, PlaybackEvent.Disconnected());
            Assert.That(state.Items.Count, Is.EqualTo(3));
            Assert.That(state.Items[1].Status, Is.EqualTo(ItemStatus.Playing));
        }

        /// <summary>切断中に再送が届いても重複排除が効く</summary>
        [Test]
        public void DedupeWorksWhileDisconnected()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));
            var commands = Run(state, new[]
            {
                PlaybackEvent.Disconnected(),
                PlaybackEvent.Received(Record(1)),
            });
            Assert.That(Only(commands, PlaybackCommandKind.FetchAudio), Is.Empty);
        }
    }
}
