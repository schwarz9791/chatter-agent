using ChatterMascot.Playback;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class PlaybackQueueAckTests : PlaybackQueueTestBase
    {
        /// <summary>再生し終えたら ack する（取得完了では出さない）</summary>
        [Test]
        public void AcksOnPlaybackCompletionOnly()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));

            var fetched = Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));
            Assert.That(Only(fetched, PlaybackCommandKind.Ack), Is.Empty);

            var played = Run(state, PlaybackEvent.Played(0, 1));
            var acks = Only(played, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
            Assert.That(acks[0].EpochId, Is.EqualTo(E1));
        }

        /// <summary>
        /// ★ ack を出した時点で、それ以下の seq は1つも残っていない。
        /// これが崩れると、サーバーの ackUpTo が「まだ喋っていない手前の entry」を消す。
        /// そこから先の任意の切断で、その文は再送されないまま失われる。
        /// </summary>
        [Test]
        public void NothingBelowAckRemains()
        {
            var state = Start(o => o.Lookahead = 3);
            Run(state, ReceivedAll(1, 2, 3, 4));

            var seen = new System.Collections.Generic.List<long>();
            foreach (var seq in new long[] { 1, 2, 3, 4 })
            {
                foreach (var command in Speak(state, seq))
                {
                    if (command.Kind != PlaybackCommandKind.Ack) continue;
                    seen.Add(command.Seq);
                    foreach (var remaining in state.Items.Keys)
                    {
                        Assert.That(remaining, Is.GreaterThan(command.Seq));
                    }
                }
            }
            Assert.That(seen, Is.EqualTo(new long[] { 1, 2, 3, 4 }));
        }

        /// <summary>★ 先読みの先で取得が失敗しても、head を追い越して ack しない</summary>
        [Test]
        public void FailureAheadDoesNotAckPastHead()
        {
            var state = Start(o => { o.Lookahead = 3; o.SynthesisAttempts = 1; });
            Run(state, ReceivedAll(1, 2, 3));

            // seq 1 を再生中にしておく
            Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));

            // seq 3 の取得が失敗。ここで ack(3) が出ると seq 1, 2 のキューが道連れになる
            var failed = Run(state, PlaybackEvent.AudioFailed(0, 3, "500"));
            Assert.That(Only(failed, PlaybackCommandKind.Ack), Is.Empty);

            // 1 → 2 と順に片付いて初めて 3 まで ack が進む
            var first = Only(Run(state, PlaybackEvent.Played(0, 1)), PlaybackCommandKind.Ack);
            Assert.That(first.Count, Is.EqualTo(1));
            Assert.That(first[0].Seq, Is.EqualTo(1L));

            var rest = Run(state, new[]
            {
                PlaybackEvent.AudioReady(0, 2, Clip(2)),
                PlaybackEvent.Played(0, 2),
            });
            // 2 の完了で 2 と（失敗済みの）3 がまとめて片付く。累積なので ack は1回
            var acks = Only(rest, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(3L));
        }

        /// <summary>連続した失敗はまとめて1回の ack にする</summary>
        [Test]
        public void ConsecutiveFailuresCollapseIntoOneAck()
        {
            var state = Start(o => { o.Lookahead = 3; o.SynthesisAttempts = 1; });
            Run(state, ReceivedAll(1, 2, 3));
            var commands = Run(state, new[]
            {
                PlaybackEvent.AudioFailed(0, 2, "500"),
                PlaybackEvent.AudioFailed(0, 3, "500"),
                PlaybackEvent.AudioFailed(0, 1, "500"),
            });
            var acks = Only(commands, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(3L));
        }

        /// <summary>切断中の ack は溜めて、再接続後に最初のフレームで送る</summary>
        [Test]
        public void HeldAckIsSentOnFirstFrameAfterReconnect()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));
            Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));

            var offline = Run(state, new[] { PlaybackEvent.Disconnected(), PlaybackEvent.Played(0, 1) });
            Assert.That(Only(offline, PlaybackCommandKind.Ack), Is.Empty);
            Assert.That(state.PendingAck.HasValue, Is.True);
            Assert.That(state.PendingAck.Value.Epoch, Is.EqualTo(0));
            Assert.That(state.PendingAck.Value.Seq, Is.EqualTo(1L));

            // ★ Connected だけでは流さない（サーバーが同じものかまだ分からない）
            Assert.That(Only(Run(state, PlaybackEvent.Connected()), PlaybackCommandKind.Ack), Is.Empty);
            Assert.That(state.PendingAck.HasValue, Is.True);

            // 同じエポックのフレームが届いて初めて、溜めていた ack が出る
            var resumed = Run(state, PlaybackEvent.Received(Record(2)));
            var acks = Only(resumed, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
            Assert.That(state.PendingAck.HasValue, Is.False);
        }

        /// <summary>
        /// ★ WebSocket の順序（接続 → 受信）で、サーバーが作り直されていたら保留 ack を出さない。
        ///
        /// Connected より先に Received を食わせるテストは <b>WebSocket が生成しえない順序</b>で、
        /// false confidence になる。実際は必ず 接続 → 受信 なので、Connected の時点では
        /// サーバーが同じものか判断できない。ここで ack を出すと、新しいサーバーの
        /// ackUpTo が配信済み・未発話の entry を消す。
        /// </summary>
        [Test]
        public void DoesNotFlushHeldAckWhenServerWasRecreated()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(5)));
            Run(state, PlaybackEvent.AudioReady(0, 5, Clip(5)));
            Run(state, new[] { PlaybackEvent.Disconnected(), PlaybackEvent.Played(0, 5) });
            Assert.That(state.PendingAck.Value.Seq, Is.EqualTo(5L));

            // 再接続。WebSocket の順序どおり Connected が先
            Assert.That(Only(Run(state, PlaybackEvent.Connected()), PlaybackCommandKind.Ack), Is.Empty);

            // そのあとで「採番がやり直された」フレームが届く
            var fresh = Run(state, PlaybackEvent.Received(Record(1, epoch: E2)));
            Assert.That(Only(fresh, PlaybackCommandKind.Ack), Is.Empty);
            Assert.That(Only(fresh, PlaybackCommandKind.DropPendingAck).Count, Is.EqualTo(1));
            Assert.That(state.PendingAck.HasValue, Is.False);
        }
    }
}
