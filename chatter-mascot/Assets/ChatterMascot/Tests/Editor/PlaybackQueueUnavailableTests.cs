using System.Collections.Generic;
using ChatterMascot.Playback;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>音声が用意できないとき（503 / 404。#29）</summary>
    [TestFixture]
    public sealed class PlaybackQueueUnavailableTests : PlaybackQueueTestBase
    {
        /// <summary>★ 503 は試行回数を消費しない（エンジンが落ちているだけでバックログを燃やさない）</summary>
        [Test]
        public void UnavailableDoesNotConsumeAttempts()
        {
            var state = Start(o => { o.SynthesisAttempts = 2; o.AudioRetryMs = 1000; });
            Run(state, PlaybackEvent.Received(Record(1)));

            // 何度 503 を受けても諦めない
            for (var i = 0; i < 10; i++)
            {
                var commands = Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0 + i * 2000);
                Assert.That(Only(commands, PlaybackCommandKind.Ack), Is.Empty);
                Run(state, PlaybackEvent.Tick(), T0 + i * 2000 + 1500);
            }
            Assert.That(state.Items[1].Status, Is.Not.EqualTo(ItemStatus.Done));
        }

        /// <summary>503 の後は AudioRetryMs だけ待ってから取り直す</summary>
        [Test]
        public void WaitsRetryIntervalAfterUnavailable()
        {
            var state = Start(o => o.AudioRetryMs = 1000);
            Run(state, PlaybackEvent.Received(Record(1)));
            Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0);

            // まだ待ち時間の中
            Assert.That(Only(Run(state, PlaybackEvent.Tick(), T0 + 500), PlaybackCommandKind.FetchAudio), Is.Empty);
            // 過ぎたら取り直す
            Assert.That(Only(Run(state, PlaybackEvent.Tick(), T0 + 1500), PlaybackCommandKind.FetchAudio).Count,
                Is.EqualTo(1));
        }

        /// <summary>★ 404 はその場で終端。head なら ack まで進む</summary>
        [Test]
        public void GoneEndsImmediately()
        {
            var state = Start();
            Run(state, PlaybackEvent.Received(Record(1)));

            var commands = Run(state, PlaybackEvent.AudioGone(0, 1, "404"));

            Assert.That(Only(commands, PlaybackCommandKind.Warn).Count, Is.GreaterThanOrEqualTo(1));
            var acks = Only(commands, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
        }

        /// <summary>転送の失敗は今までどおり1回リトライして諦める</summary>
        [Test]
        public void TransportFailureRetriesOnce()
        {
            var state = Start(o => o.SynthesisAttempts = 2);
            Run(state, PlaybackEvent.Received(Record(1)));

            var retried = Run(state, PlaybackEvent.AudioFailed(0, 1, "ECONNREFUSED"));
            Assert.That(Only(retried, PlaybackCommandKind.FetchAudio).Count, Is.EqualTo(1));
            Assert.That(Only(retried, PlaybackCommandKind.Ack), Is.Empty);

            var given = Run(state, PlaybackEvent.AudioFailed(0, 1, "ECONNREFUSED"));
            var acks = Only(given, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
        }

        /// <summary>★ 用意できない状態が続いたら、設定を疑う手がかりを出す</summary>
        [Test]
        public void EmitsConfigHintWhenUnavailablePersists()
        {
            var state = Start(o => { o.UnavailableWarnAfter = 3; o.AudioRetryMs = 0; o.AudioRetryMaxMs = 0; });
            Run(state, ReceivedAll(1, 2, 3, 4, 5));

            var warned = 0;
            foreach (var seq in new long[] { 1, 2, 3, 4, 5 })
            {
                warned += Hints(Run(state, PlaybackEvent.AudioGone(0, seq, "404"))).Count;
            }

            Assert.That(warned, Is.EqualTo(1));
        }

        /// <summary>
        /// ★ 用意できない状態が続くなら、UnavailableWarnRepeatMs ごとに出し直す。
        /// bool のラッチだと、長く走らせたときに「停止 → 復旧 → 再停止」を見ても
        /// 最初の1回しか出さない。
        /// </summary>
        [Test]
        public void RearmsHintByTime()
        {
            var state = Start(o =>
            {
                o.UnavailableWarnAfter = 1;
                o.UnavailableWarnRepeatMs = 60000;
                o.AudioRetryMaxMs = 0;
            });
            Run(state, ReceivedAll(1, 2));

            Assert.That(Hints(Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0)).Count, Is.EqualTo(1));
            // 間隔の中では出さない
            Assert.That(Hints(Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0 + 10000)), Is.Empty);
            // 過ぎたら出し直す
            Assert.That(Hints(Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0 + 70000)).Count, Is.EqualTo(1));
        }

        /// <summary>★ 503 が続く間は取り直しの間隔を倍にする（窓ぶんのリクエストが飛び続けない）</summary>
        [Test]
        public void BacksOffExponentially()
        {
            var state = Start(o => { o.AudioRetryMs = 1000; o.AudioRetryMaxMs = 8000; });
            Run(state, PlaybackEvent.Received(Record(1)));

            var waits = new List<long>();
            for (var i = 0; i < 5; i++)
            {
                Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0);
                var retryAfter = state.Items[1].RetryAfter;
                waits.Add(retryAfter - T0);
                // 次の取得を走らせる（バックオフが明けた体で）
                Run(state, PlaybackEvent.Tick(), retryAfter + 1);
            }

            Assert.That(waits, Is.EqualTo(new long[] { 1000, 2000, 4000, 8000, 8000 }));
        }

        /// <summary>
        /// ★ 503 が長く続いてもバックオフが壊れない。
        ///
        /// シフト量を頭打ちにしないと 2^step が long を溢れて<b>負の待ち時間</b>になり、
        /// バックオフが完全に無効化される（＝1フレームごとに取り直しが飛ぶ）。
        /// エンジンを起動し忘れたまま放置したときに、いちばん静かに壊れる。
        /// </summary>
        [Test]
        public void BackoffSurvivesLongOutage()
        {
            var state = Start(o => { o.AudioRetryMs = 1000; o.AudioRetryMaxMs = 30000; });
            Run(state, PlaybackEvent.Received(Record(1)));

            for (var i = 0; i < 200; i++)
            {
                Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0);
                var retryAfter = state.Items[1].RetryAfter;
                Assert.That(retryAfter, Is.GreaterThan(T0), "i=" + i + " でバックオフが過去になった");
                Assert.That(retryAfter - T0, Is.LessThanOrEqualTo(30000L), "i=" + i + " で上限を超えた");
                Run(state, PlaybackEvent.Tick(), retryAfter + 1);
            }
        }

        /// <summary>★ 音声が取れたらバックオフも警告のラッチも解ける</summary>
        [Test]
        public void SuccessClearsBackoffAndLatch()
        {
            var state = Start(o =>
            {
                o.AudioRetryMs = 1000;
                o.AudioRetryMaxMs = 8000;
                o.UnavailableWarnAfter = 1;
            });
            Run(state, ReceivedAll(1, 2));

            Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0);
            Run(state, PlaybackEvent.Tick(), T0 + 2000);
            Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"), T0 + 2000);
            Assert.That(state.UnavailableBackoffStep, Is.EqualTo(2));

            // バックオフが明けてから取り直させる（AudioReady は Fetching の item にしか効かない）
            Run(state, PlaybackEvent.Tick(), T0 + 5000);
            Assert.That(state.Items[1].Status, Is.EqualTo(ItemStatus.Fetching));
            Run(state, PlaybackEvent.AudioReady(0, 1, Clip(1)), T0 + 5000);

            Assert.That(state.UnavailableBackoffStep, Is.EqualTo(0));
            Assert.That(state.UnavailableWarnedAt, Is.EqualTo(0));
            Assert.That(state.UnavailableStreak, Is.EqualTo(0));
        }

        /// <summary>★ 503 は何度来ても試行回数を消費しない（数えるのは AudioFailed だけ）</summary>
        [Test]
        public void OnlyTransportFailureCounts()
        {
            var state = Start(o => { o.SynthesisAttempts = 2; o.AudioRetryMs = 0; o.AudioRetryMaxMs = 0; });
            Run(state, PlaybackEvent.Received(Record(1)));

            for (var i = 0; i < 20; i++)
            {
                Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"));
                Run(state, PlaybackEvent.Tick());
            }
            Assert.That(state.Items[1].Attempts, Is.EqualTo(0));
            Assert.That(state.Items[1].Status, Is.Not.EqualTo(ItemStatus.Done));

            // 転送失敗だけが数えられ、2回で諦める
            Run(state, PlaybackEvent.AudioFailed(0, 1, "ECONNRESET"));
            Assert.That(state.Items[1].Attempts, Is.EqualTo(1));
            var given = Run(state, PlaybackEvent.AudioFailed(0, 1, "ECONNRESET"));
            var acks = Only(given, PlaybackCommandKind.Ack);
            Assert.That(acks.Count, Is.EqualTo(1));
            Assert.That(acks[0].Seq, Is.EqualTo(1L));
        }

        /// <summary>音声が取れたら連続の数え直し</summary>
        [Test]
        public void StreakResetsOnSuccess()
        {
            var state = Start(o => { o.UnavailableWarnAfter = 2; o.AudioRetryMs = 0; });
            Run(state, ReceivedAll(1, 2, 3));

            Run(state, PlaybackEvent.AudioUnavailable(0, 1, "503"));
            Run(state, PlaybackEvent.AudioReady(0, 2, Clip(2)));
            var commands = Run(state, PlaybackEvent.AudioUnavailable(0, 3, "503"));

            Assert.That(Only(commands, PlaybackCommandKind.Warn), Is.Empty);
            Assert.That(state.UnavailableStreak, Is.EqualTo(1));
        }

        /// <summary>「設定を疑え」の手がかりだけを拾う。</summary>
        private static List<PlaybackCommand> Hints(IEnumerable<PlaybackCommand> commands)
        {
            var result = new List<PlaybackCommand>();
            foreach (var c in Only(commands, PlaybackCommandKind.Warn))
            {
                if (c.Message != null && c.Message.Contains("ttsSpeakerId")) result.Add(c);
            }
            return result;
        }
    }
}
