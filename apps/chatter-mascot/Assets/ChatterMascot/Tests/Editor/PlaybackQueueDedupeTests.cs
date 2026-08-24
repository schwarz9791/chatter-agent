using System.Collections.Generic;
using ChatterMascot.Playback;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class PlaybackQueueDedupeTests : PlaybackQueueTestBase
    {
        /// <summary>再送された同じ (epoch, seq) を二度読み上げない</summary>
        [Test]
        public void DoesNotSpeakSameKeyTwice()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));
            Speak(state, 1);

            var again = Run(state, PlaybackEvent.Received(Record(1)));
            Assert.That(Only(again, PlaybackCommandKind.FetchAudio), Is.Empty);
            Assert.That(Only(again, PlaybackCommandKind.Play), Is.Empty);
        }

        /// <summary>
        /// ★ 消費済みが再送されたら ack を打ち直す（サーバー側に残っている証拠なので）。
        /// ack が届く前に切断された / サーバー再起動で配信済みの記憶が空になった、のどちらか。
        /// 打ち直さないと entry が永久に残り、再接続のたびに再送され続ける。
        /// </summary>
        [Test]
        public void ReacksOnResendOfConsumed()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));
            Speak(state, 1);

            var again = Run(state, PlaybackEvent.Received(Record(1)));
            var acks = Only(again, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
            Assert.That(acks[0].EpochId, Is.EqualTo(E1));
        }

        /// <summary>処理中のものが再送されても取得をやり直さない</summary>
        [Test]
        public void DoesNotRefetchInFlight()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));
            var again = Run(state, PlaybackEvent.Received(Record(1)));
            Assert.That(Only(again, PlaybackCommandKind.FetchAudio), Is.Empty);
            Assert.That(state.Items[1].Status, Is.EqualTo(ItemStatus.Fetching));
        }

        /// <summary>消費済みの記憶は上限で古い順に落ちる</summary>
        [Test]
        public void SeenIsEvictedInInsertionOrder()
        {
            var state = Start(o => o.SeenCapacity = 3);
            foreach (var seq in new long[] { 1, 2, 3, 4 })
            {
                Run(state, PlaybackEvent.Received(Record(seq)));
                Speak(state, seq);
            }
            Assert.That(state.Seen.Count, Is.EqualTo(3));
            // 最初に消費した seq 1 は忘れている
            Assert.That(state.Seen.Contains(E1 + ":1"), Is.False);
            Assert.That(state.Seen.Contains(E1 + ":4"), Is.True);
        }

        /// <summary>
        /// ★ seen から溢れた消費済みの再送を、採番のやり直しと取り違えない。
        /// SeenCapacity はサーバー側の speechQueueMaxEntries とズレうるので溢れは起きる。
        /// ここを ResetEpoch に落とすと<b>同じ文を2回喋る</b>。
        /// </summary>
        [Test]
        public void OverflowedSeenIsNotMistakenForEpochReset()
        {
            var state = Start(o => o.SeenCapacity = 3);
            foreach (var seq in new long[] { 1, 2, 3, 4, 5 })
            {
                Run(state, PlaybackEvent.Received(Record(seq)));
                Speak(state, seq);
            }
            // seq 1 は seen から溢れている
            Assert.That(state.Seen.Contains(E1 + ":1"), Is.False);

            // その seq 1 が **元の ts のまま** 再送される
            var resent = Run(state, PlaybackEvent.Received(Record(1)));
            Assert.That(Only(resent, PlaybackCommandKind.FetchAudio), Is.Empty);
            Assert.That(Only(resent, PlaybackCommandKind.Warn), Is.Empty);
            var acks = Only(resent, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
            Assert.That(state.Epoch, Is.EqualTo(0));
        }

        /// <summary>
        /// ★ 追いつきが seq 昇順で来なくても、未消費のフレームを捨てない。
        /// 受信ベースの水位だけで「seq が戻った」を判定すると、順序が乱れただけの
        /// 未消費フレームを再送と誤読して<b>無音になる</b>。
        /// </summary>
        [Test]
        public void OutOfOrderCatchUpKeepsAllFrames()
        {
            var state = Start(o => o.Lookahead = 3);
            var commands = Run(state, ReceivedAll(3, 1, 2));
            var seqs = new List<long>(SeqsOf(commands, PlaybackCommandKind.FetchAudio));
            seqs.Sort();
            Assert.That(seqs, Is.EqualTo(new long[] { 1, 2, 3 }));
            Assert.That(state.Epoch, Is.EqualTo(0));
        }
    }
}
