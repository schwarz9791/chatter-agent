using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="BlinkTimer"/>。乱数と時計を注入する純粋な時間関数なので、決定的に固定できる。
    ///
    /// ★ <b>コルーチンにしなかったのはこのため。</b> UniVRM のサンプル（<c>VRM10Blinker</c>、
    ///   <c>Samples~</c> にあり Package Manager から明示的にインポートしないと読まれない）は
    ///   <c>StartCoroutine</c> + <c>WaitForSeconds</c> なので EditMode から回せない。
    ///
    /// 既定は 間隔 2〜6秒（cc-mascot）/ 閉 0.1・保持 0.06・開 0.03秒（VRM10Blinker）。
    /// ★ ここでは <c>random</c> を 0（＝最短の 2秒）か 1（＝最長の 6秒）に固定して使う。
    /// </summary>
    [TestFixture]
    public sealed class BlinkTimerTests
    {
        private const float Close = 0.1f;
        private const float Hold = 0.06f;
        private const float Open = 0.03f;

        private static BlinkTimer Timer(float random)
        {
            return new BlinkTimer(() => random, 2f, 6f, Close, Hold, Open);
        }

        [Test]
        public void StaysClosedUntilTheIntervalElapses()
        {
            var timer = Timer(0f); // 間隔 = 2秒
            timer.Tick(0.0);

            Assert.That(timer.Tick(0.5), Is.EqualTo(0f));
            Assert.That(timer.Tick(1.99), Is.EqualTo(0f));
        }

        /// <summary>閉（0→1）→ 保持（1）→ 開（1→0）→ 待ち（0）の順になること。</summary>
        [Test]
        public void ClosesThenHoldsThenOpens()
        {
            var timer = Timer(0f); // 2秒後に瞬く
            timer.Tick(0.0);

            // ★ フェーズ境界ちょうどを踏まないこと。秒数は float なので double へ広げると
            //   ±1ulp ずれ、「>= で進んだか」がプラットフォーム依存の当落線上になる
            Assert.That(timer.Tick(2.0 + Close * 0.25), Is.EqualTo(0.25f).Within(1e-4f), "閉じの途中");
            Assert.That(timer.Tick(2.0 + Close * 0.75), Is.EqualTo(0.75f).Within(1e-4f), "閉じの途中");
            Assert.That(timer.Tick(2.0 + Close + Hold * 0.5), Is.EqualTo(1f), "保持");
            Assert.That(timer.Tick(2.0 + Close + Hold + Open * 0.5), Is.EqualTo(0.5f).Within(1e-4f), "開きの途中");
            Assert.That(timer.Tick(2.0 + Close + Hold + Open + 0.01), Is.EqualTo(0f), "開き切り＝待ちへ");
        }

        [Test]
        public void StaysWithinZeroAndOne()
        {
            var timer = Timer(0f);

            for (var i = 0; i <= 2000; i++)
            {
                var value = timer.Tick(i * 0.01);
                Assert.That(value, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f), $"i={i}");
            }
        }

        /// <summary>
        /// ★ 間隔は <c>[min, max]</c> に収まること。サンプルの <c>Random.value * Interval</c> は
        /// 下限が無く 0秒近い間隔が出るので、そちらは採っていない。
        /// </summary>
        [Test]
        public void IntervalStaysWithinTheConfiguredRange()
        {
            var shortest = Timer(0f);
            shortest.Tick(0.0);
            Assert.That(shortest.Tick(1.99), Is.EqualTo(0f));
            Assert.That(shortest.Tick(2.0 + Close * 0.5), Is.GreaterThan(0f), "min = 2秒");

            var longest = Timer(1f);
            longest.Tick(0.0);
            Assert.That(longest.Tick(5.99), Is.EqualTo(0f));
            Assert.That(longest.Tick(6.0 + Close * 0.5), Is.GreaterThan(0f), "max = 6秒");
        }

        /// <summary>
        /// <c>kind: "prompt"</c> の到着で1回瞬かせるための口。
        /// ★ 待ちが 6秒残っていても打ち切って閉じ始める。
        /// </summary>
        [Test]
        public void RequestBlinksWithoutWaitingForTheInterval()
        {
            var timer = Timer(1f); // 次の瞬きは 6秒後
            timer.Tick(0.0);
            Assert.That(timer.Tick(1.0), Is.EqualTo(0f));

            timer.Request();

            timer.Tick(1.0);
            Assert.That(timer.Tick(1.0 + Close * 0.5), Is.EqualTo(0.5f).Within(1e-4f));
        }

        /// <summary>
        /// ★ <c>VRM10Blinker.Request</c> の setter と同じ1秒のデバウンス。<b>保険</b>であって、
        /// 呼び出し側はエッジで1回だけ呼ぶ（毎フレーム呼ぶと1秒ごとに瞬き続ける）。
        /// </summary>
        [Test]
        public void RequestIsDebouncedForOneSecond()
        {
            var timer = Timer(1f); // 自然な瞬きは 6秒後なので混ざらない
            timer.Tick(0.0);

            timer.Request();
            timer.Tick(1.0);                                   // 1回目を消費
            timer.Tick(1.0 + Close + Hold + Open + 0.01);      // 瞬き終わり＝待ちへ戻る

            timer.Request();
            Assert.That(timer.Tick(1.5), Is.EqualTo(0f), "1秒経っていないので捨てられる");
            Assert.That(timer.Tick(1.5 + Close * 0.5), Is.EqualTo(0f));

            timer.Request();
            timer.Tick(2.5);
            Assert.That(timer.Tick(2.5 + Close * 0.5), Is.EqualTo(0.5f).Within(1e-4f), "1秒経っていれば通る");
        }

        [Test]
        public void RequestDuringABlinkIsDropped()
        {
            var timer = Timer(1f);
            timer.Tick(0.0);

            timer.Request();
            timer.Tick(1.0);
            // 閉じている最中に追加要求が来ても、瞬きが伸びたりやり直したりしない
            timer.Request();
            Assert.That(timer.Tick(1.0 + Close + Hold * 0.5), Is.EqualTo(1f));
            Assert.That(timer.Tick(1.0 + Close + Hold + Open + 0.01), Is.EqualTo(0f));
        }

        /// <summary>
        /// キルスイッチ。★ 再び有効にしたときは<b>そのときの時刻から待ちをやり直す</b>
        /// （止めていた間の経過を持ち越すと、再開した瞬間に瞬く）。
        /// </summary>
        [Test]
        public void DisabledStaysAtZeroAndRestartsTheIntervalOnResume()
        {
            var timer = Timer(0f);
            timer.Tick(0.0);

            timer.Enabled = false;
            Assert.That(timer.Tick(10.0), Is.EqualTo(0f));
            Assert.That(timer.Tick(100.0), Is.EqualTo(0f));

            timer.Enabled = true;
            Assert.That(timer.Tick(100.0), Is.EqualTo(0f));
            Assert.That(timer.Tick(101.9), Is.EqualTo(0f), "再開した時刻から 2秒待つ");
            Assert.That(timer.Tick(102.0 + Close * 0.5), Is.EqualTo(0.5f).Within(1e-4f));
        }

        /// <summary>
        /// ★ <b>常駐アプリなので <c>now</c> は日単位まで伸びる。</b> 経過時間を <c>(float)now</c> から
        /// 作ると、7日で float の刻み幅が1フレームぶんの差を上回り、瞬きがカクつく／止まる
        /// （<c>OscillatorTests</c> が位相について固定しているのと同じ趣旨）。
        /// </summary>
        [Test]
        public void KeepsResolutionAfterDaysOfUptime()
        {
            var timer = Timer(0f);
            var start = 7.0 * 24.0 * 60.0 * 60.0; // 7日
            timer.Tick(start);

            var a = timer.Tick(start + 2.0 + Close * 0.2);
            var b = timer.Tick(start + 2.0 + Close * 0.5);

            Assert.That(a, Is.EqualTo(0.2f).Within(1e-3f));
            Assert.That(b, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(b, Is.GreaterThan(a));
        }

        /// <summary>
        /// ★ 長く止めていた（あるいは 0 秒フェーズが並んだ）ときに固まらないこと。
        /// フェーズを進める回数に上限を置いてある。
        /// </summary>
        [Test]
        public void DoesNotHangOnZeroLengthPhases()
        {
            var timer = new BlinkTimer(() => 0f, 0f, 0f, 0f, 0f, 0f);

            for (var i = 0; i < 10; i++)
            {
                var value = timer.Tick(i);
                Assert.That(value, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f));
            }
        }

        [Test]
        public void DoesNotHangAfterALongPause()
        {
            var timer = Timer(0f);
            timer.Tick(0.0);

            // 1時間ぶん飛ばす（アプリがスリープから戻った形）
            var value = timer.Tick(3600.0);
            Assert.That(value, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f));

            // その後も普通に瞬く
            var blinked = false;
            for (var i = 0; i <= 800; i++)
            {
                if (timer.Tick(3600.0 + i * 0.01) > 0f) blinked = true;
            }
            Assert.That(blinked, Is.True);
        }
    }
}
