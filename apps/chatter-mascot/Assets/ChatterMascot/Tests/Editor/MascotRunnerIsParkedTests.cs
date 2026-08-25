using ChatterMascot;
using ChatterMascot.Playback;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// 「503 のバックオフで停車中か」の判定。
    ///
    /// ★ ここを間違えると<b>症状が両極に振れる</b>: 停車中を数えてしまうと
    ///   合成エンジンが落ちている間ずっとデバイスを掴み続け（この機能がいちばん
    ///   得をするはずの場面で効かない）、逆に数えなさすぎると鳴る直前に手放して
    ///   <b>1文目の頭が切れる</b>。
    /// </summary>
    [TestFixture]
    public sealed class MascotRunnerIsParkedTests
    {
        private static QueueItem Item(ItemStatus status, long retryAfter)
        {
            return new QueueItem { Status = status, RetryAfter = retryAfter };
        }

        [Test]
        public void ParkedWhilePendingAndBeforeRetryAfter()
        {
            Assert.That(MascotRunner.IsParked(Item(ItemStatus.Pending, 2000), 1999), Is.True);
        }

        [Test]
        public void NotParkedOnceRetryAfterHasPassed()
        {
            // 境界は「到達したら停車ではない」（PlaybackQueue.FillWindow の now < RetryAfter と揃える）
            Assert.That(MascotRunner.IsParked(Item(ItemStatus.Pending, 2000), 2000), Is.False);
            Assert.That(MascotRunner.IsParked(Item(ItemStatus.Pending, 2000), 2001), Is.False);
        }

        /// <summary>
        /// 503 を受けていない <c>Pending</c>（まだ取りに行っていないだけ）は
        /// 「まもなく鳴る」。<c>RetryAfter</c> は 0 のまま。
        /// </summary>
        [Test]
        public void NotParkedWithoutRetryAfter()
        {
            Assert.That(MascotRunner.IsParked(Item(ItemStatus.Pending, 0), 1000), Is.False);
        }

        /// <summary>
        /// ★ <c>Pending</c> 以外は停車ではない。取得や再生が進んでいるので、
        /// <c>RetryAfter</c> が残っていても数える。
        /// </summary>
        [Test]
        public void OnlyPendingCanBeParked()
        {
            var statuses = new[]
            {
                ItemStatus.Fetching, ItemStatus.Ready, ItemStatus.Playing, ItemStatus.Done,
            };
            foreach (var status in statuses)
            {
                Assert.That(
                    MascotRunner.IsParked(Item(status, 2000), 1999), Is.False, status.ToString());
            }
        }

        [Test]
        public void NullIsNotParked()
        {
            Assert.That(MascotRunner.IsParked(null, 1000), Is.False);
        }
    }
}
