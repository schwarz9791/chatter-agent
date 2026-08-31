using ChatterMascot.Window;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class SavePolicyTests
    {
        private const int MaxFailures = 3;
        private static readonly PointRect Rect = new PointRect(1770f, 1598f, 300f, 480f);

        private static WindowState State(PointRect rect, string signature) => new WindowState(rect, signature);

        private static SaveAction Decide(
            WindowState candidate, WindowState lastPersisted,
            int failures = 0, double now = 100, double retryAt = 0) =>
            SavePolicy.Decide(candidate, lastPersisted, failures, MaxFailures, now, retryAt);

        [Test]
        public void WritesWhenNothingHasBeenPersistedYet()
        {
            Assert.That(Decide(State(Rect, "sig"), WindowState.None), Is.EqualTo(SaveAction.Write));
        }

        [Test]
        public void SkipsWhenTheRectAndTheLayoutAreUnchanged()
        {
            var state = State(Rect, "sig");

            Assert.That(Decide(state, state), Is.EqualTo(SaveAction.Skip));
        }

        /// <summary>
        /// ★ <b>これが指紋を比べる理由。</b> 窓が動かないまま構成だけ変わることがある
        /// （マスコットを置いていない側のディスプレイを抜き差しする、など）。矩形だけで
        /// 短絡すると古い指紋が残り続け、次の起動で厳しい方の閾値が使われて
        /// <b>ユーザーが端に寄せた窓が押し戻される</b>。
        /// </summary>
        [Test]
        public void WritesWhenOnlyTheLayoutChanged()
        {
            Assert.That(Decide(State(Rect, "新しい構成"), State(Rect, "前の構成")),
                        Is.EqualTo(SaveAction.Write));
        }

        /// <summary>丸めの差は「変わった」に数えない（書いた直後の読み戻しで往復しないため）。</summary>
        [Test]
        public void SkipsARoundingDifference()
        {
            var moved = new PointRect(Rect.X + 0.4f, Rect.Y, Rect.Width, Rect.Height);

            Assert.That(Decide(State(moved, "sig"), State(Rect, "sig")), Is.EqualTo(SaveAction.Skip));
        }

        [Test]
        public void SkipsAnInvalidRect()
        {
            Assert.That(Decide(State(new PointRect(0f, 0f, 0f, 0f), "sig"), WindowState.None),
                        Is.EqualTo(SaveAction.Skip));
        }

        // ── 失敗したあと ────────────────────────────────────────────

        /// <summary>
        /// ★ <b>待たせること。</b> 失敗しても保留を落とさない設計なので、待たせないと
        /// <b>毎フレーム書き込みを試して警告が洪水になる</b>。
        /// </summary>
        [Test]
        public void WaitsAfterAFailure()
        {
            Assert.That(Decide(State(Rect, "sig"), WindowState.None, failures: 1, now: 100, retryAt: 105),
                        Is.EqualTo(SaveAction.WaitForRetry));
        }

        [Test]
        public void RetriesOnceTheWaitIsOver()
        {
            Assert.That(Decide(State(Rect, "sig"), WindowState.None, failures: 1, now: 105, retryAt: 105),
                        Is.EqualTo(SaveAction.Write));
        }

        /// <summary>★ 上限を置かないと、書けない状態が続く限り永遠に鳴り続ける。</summary>
        [Test]
        public void GivesUpAfterTooManyFailures()
        {
            Assert.That(Decide(State(Rect, "sig"), WindowState.None, failures: MaxFailures, now: 999),
                        Is.EqualTo(SaveAction.Skip));
        }

        /// <summary>
        /// ★ 諦めた後に内容が変わっても蒸し返さない。書けない原因は内容ではないので、
        /// 蒸し返すと警告が復活する。
        /// </summary>
        [Test]
        public void StaysGivenUpEvenWhenTheContentChanges()
        {
            var moved = new PointRect(0f, 0f, 300f, 480f);

            Assert.That(Decide(State(moved, "別の構成"), State(Rect, "sig"),
                               failures: MaxFailures, now: 999),
                        Is.EqualTo(SaveAction.Skip));
        }

        /// <summary>0 以下はキルスイッチ（諦めない）。</summary>
        [Test]
        public void ZeroMaxFailuresNeverGivesUp()
        {
            Assert.That(SavePolicy.Decide(State(Rect, "sig"), WindowState.None, 99, 0, 999, 0),
                        Is.EqualTo(SaveAction.Write));
        }
    }
}
