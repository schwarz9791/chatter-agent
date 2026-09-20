using ChatterMascot.Playback;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 採番のやり直し（エポック変化）。ランタイムルートが消えると CLI の採番は 1 に戻る。
    /// </summary>
    [TestFixture]
    public sealed class PlaybackQueueEpochTests : PlaybackQueueTestBase
    {
        /// <summary>
        /// ★ 旧エポックで消費済みの seq でも、epoch が違えば喋る。
        /// seq だけで覚えていると、ランタイムルートを消した後に
        /// <b>何百文でも一切喋らず、エラーも出ない</b>という最悪の症状になる。
        /// </summary>
        [Test]
        public void SpeaksAgainWhenEpochDiffers()
        {
            var state = Start();
            foreach (var seq in new long[] { 1, 2, 3 })
            {
                Run(state, PlaybackEvent.Received(Record(seq)));
                Speak(state, seq);
            }

            var fresh = Record(1, epoch: E2, text: "新しい1。");
            var commands = Run(state, PlaybackEvent.Received(fresh));
            // エポックが1つ進んでいるので、取得も新しいエポックで走る
            var fetches = Only(commands, PlaybackCommandKind.FetchAudio);
            Assert.That(fetches.Count, Is.EqualTo(1));
            Assert.That(fetches[0].Epoch, Is.EqualTo(1));
            Assert.That(fetches[0].Seq, Is.EqualTo(1L));
            Assert.That(fetches[0].Path, Is.EqualTo(AudioPathOf(E2, 1)));
            Assert.That(state.Epoch, Is.EqualTo(1));
        }

        /// <summary>
        /// ★ エポックが変わったら保留 ack を捨てる。
        /// 旧エポックの ack(500) を新エポックのサーバーに打つと、ackUpTo がファイル名で
        /// 範囲削除するため、まだ喋っていない新しい seq 1, 2 が消える。
        /// </summary>
        [Test]
        public void DropsPendingAckOnEpochChange()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(5)));
            Run(state, PlaybackEvent.AudioReady(0, 5, Clip(5)));
            Run(state, new[] { PlaybackEvent.Disconnected(), PlaybackEvent.Played(0, 5) });
            Assert.That(state.PendingAck.Value.Seq, Is.EqualTo(5L));

            Run(state, PlaybackEvent.Received(Record(1, epoch: E2)));
            Assert.That(state.PendingAck.HasValue, Is.False);

            var online = Run(state, PlaybackEvent.Connected());
            Assert.That(Only(online, PlaybackCommandKind.Ack), Is.Empty);
        }

        /// <summary>エポックが変わったら取得待ちを捨て、音声も解放する</summary>
        [Test]
        public void DiscardsInFlightOnEpochChange()
        {
            var state = Start(o => o.Lookahead = 3);
            Run(state, ReceivedAll(2, 3));
            Run(state, PlaybackEvent.AudioReady(0, 3, Clip(3)));

            var commands = Run(state, PlaybackEvent.Received(Record(1, epoch: E2)));
            var discards = Only(commands, PlaybackCommandKind.DiscardAudio);
            Assert.That(discards.Count, Is.EqualTo(1));
            Assert.That(discards[0].Epoch, Is.EqualTo(0));
            Assert.That(discards[0].Seq, Is.EqualTo(3L));
            Assert.That(discards[0].Audio, Is.EqualTo(Clip(3)));
            Assert.That(Only(commands, PlaybackCommandKind.Warn).Count, Is.EqualTo(1));
            Assert.That(state.Items.ContainsKey(2), Is.False);
            Assert.That(state.Items.ContainsKey(3), Is.False);
        }

        /// <summary>★ 再生中の音は最後まで流すが、その完了では ack しない</summary>
        [Test]
        public void OrphanPlaybackFinishesWithoutAck()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(5)));
            Run(state, PlaybackEvent.AudioReady(0, 5, Clip(5)));
            Run(state, PlaybackEvent.Received(Record(1, epoch: E2)));

            var finished = Run(state, PlaybackEvent.Played(0, 5));
            Assert.That(Only(finished, PlaybackCommandKind.Ack), Is.Empty);
            // 音声の後始末だけは行う
            var discards = Only(finished, PlaybackCommandKind.DiscardAudio);
            Assert.That(discards.Count, Is.EqualTo(1));
            Assert.That(discards[0].Epoch, Is.EqualTo(0));
            Assert.That(discards[0].Seq, Is.EqualTo(5L));
        }

        /// <summary>
        /// ★ 旧エポックの取得結果を新しい item が拾わない。
        /// seq だけで突き合わせると、「こんにちは」を鳴らしながら「さようなら」を ack する。
        /// </summary>
        [Test]
        public void LateResultFromOldEpochIsNotAdopted()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1, text: "こんにちは。")));
            Run(state, PlaybackEvent.Received(Record(1, epoch: E2, text: "さようなら。")));
            Assert.That(state.Epoch, Is.EqualTo(1));

            // 旧エポックで投げた取得が今ごろ返ってくる
            var late = Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)));
            Assert.That(Only(late, PlaybackCommandKind.Play), Is.Empty);
            var discards = Only(late, PlaybackCommandKind.DiscardAudio);
            Assert.That(discards.Count, Is.EqualTo(1));
            Assert.That(discards[0].Epoch, Is.EqualTo(0));
            // 新しい item は取得待ちのまま。古い音声を掴んでいない
            Assert.That(state.Items[1].Status, Is.EqualTo(ItemStatus.Fetching));
            Assert.That(state.Items[1].Audio, Is.Null);
        }

        /// <summary>
        /// ★ 音声ハンドルがエポックを跨いで衝突しない。
        /// Play / DiscardAudio / FetchAudio がすべて (epoch, seq) を持つので、
        /// ドライバは同じ seq でも別のハンドルを保てる。
        /// </summary>
        [Test]
        public void HandlesDoNotCollideAcrossEpochs()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));
            var first = Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1, 0)));
            var plays = Only(first, PlaybackCommandKind.Play);
            Assert.That(plays.Count, Is.EqualTo(1));
            Assert.That(plays[0].Epoch, Is.EqualTo(0));
            Assert.That(plays[0].Audio, Is.EqualTo(Clip(1, 0)));

            // 再生中に採番がやり直される → 旧 item は orphan
            Run(state, PlaybackEvent.Received(Record(1, epoch: E2)));
            var second = Run(state, PlaybackEvent.AudioReady(1, 1, Clip(1, 1)));
            var plays2 = Only(second, PlaybackCommandKind.Play);
            Assert.That(plays2.Count, Is.EqualTo(1));
            Assert.That(plays2[0].Epoch, Is.EqualTo(1));
            Assert.That(plays2[0].Audio, Is.EqualTo(Clip(1, 1)));

            // 旧エポックの再生完了は orphan として処理され、**新しい item の完了を飲まない**
            var orphanDone = Run(state, PlaybackEvent.Played(0, 1));
            Assert.That(Only(orphanDone, PlaybackCommandKind.Ack), Is.Empty);
            var discards = Only(orphanDone, PlaybackCommandKind.DiscardAudio);
            Assert.That(discards.Count, Is.EqualTo(1));
            Assert.That(discards[0].Audio, Is.EqualTo(Clip(1, 0)));
            Assert.That(state.Items[1].Status, Is.EqualTo(ItemStatus.Playing));
        }

        /// <summary>
        /// ★ 世代の判定は epoch 一本。ts が動いてもエポック変化にしない。
        /// #30 で1メッセージ内の ts が同値になったこともあり、ts は世代の指標として当てにならない。
        /// </summary>
        [Test]
        public void TsChangeIsNotAnEpochChange()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));

            var commands = Run(state, PlaybackEvent.Received(Record(1, ts: "2026-08-16T00:00:00.000Z")));

            Assert.That(Only(commands, PlaybackCommandKind.Warn), Is.Empty);
            Assert.That(state.Epoch, Is.EqualTo(0));
            // 同じ世代の同じ seq は同じ文。取得をやり直さず、最初に受けたレコードのまま
            Assert.That(Only(commands, PlaybackCommandKind.FetchAudio), Is.Empty);
            Assert.That(state.Items[1].Record.Ts, Is.EqualTo(Record(1).Ts));
        }

        /// <summary>★ epoch が変われば、ts が戻っていてもエポック変化として扱う</summary>
        [Test]
        public void EpochChangeWinsOverGoingBackInTime()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(5)));

            // 新しい世代の seq 1。ts は前の世代より**古い**（バックアップ復元などで起こりうる）
            var commands = Run(state, PlaybackEvent.Received(
                Record(1, epoch: E2, ts: "2020-01-01T00:00:00.000Z")));

            Assert.That(Only(commands, PlaybackCommandKind.Warn).Count, Is.EqualTo(1));
            Assert.That(state.Epoch, Is.EqualTo(1));
            Assert.That(state.EpochId, Is.EqualTo(E2));
            Assert.That(state.Items.ContainsKey(5), Is.False);
        }
    }
}
