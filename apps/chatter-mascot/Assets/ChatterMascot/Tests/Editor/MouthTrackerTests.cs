using ChatterMascot.Vrm;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="MouthTracker"/>。生の RMS → 口の weight（区間の始点・ゲイン・attack/release）。
    ///
    /// ★ <b>ここが <c>MonoBehaviour</c> に書かれていたらテストは1行も当たらない</b>
    ///   （<c>ChatterMascot.Tests.asmdef</c> は <c>ChatterMascot.Runtime</c> しか参照しない）。
    ///   <see cref="FaceLatch"/> と同じ理由で <c>Runtime/Vrm/</c> に置いてある。
    /// </summary>
    [TestFixture]
    public sealed class MouthTrackerTests
    {
        private const float Gain = 4f;
        private const float Release = 8f;

        /// <summary>
        /// ★★ <b>初回は点サンプル。</b> 前フレームの時刻を <c>-∞</c> で初期化すると、
        ///   <c>SpeakingSet.Mouth(-∞, now)</c> が<b>エンベロープ全体の最大</b>を返して
        ///   最初のフレームで口が全開に飛ぶ。
        /// </summary>
        [Test]
        public void FirstSampleIsAPoint()
        {
            var tracker = new MouthTracker();

            Assert.That(tracker.From(100.0), Is.EqualTo(100.0));
        }

        /// <summary>2フレーム目からは区間になる（前フレームの時刻から今まで）。</summary>
        [Test]
        public void LaterSamplesSpanTheFrame()
        {
            var tracker = new MouthTracker();
            tracker.Tick(0f, 100.0, Gain, Release, 0.033f);

            Assert.That(tracker.From(100.033), Is.EqualTo(100.0).Within(1e-9));
        }

        /// <summary>
        /// ★★ <b>フレームが来なかった後は区間を切ること。</b> 切らないと、復帰フレームで
        ///   数秒ぶんのエンベロープの最大（＝実際の振幅と無関係に ~1.0）を取り、
        ///   <c>release</c> で閉じるまで約 125ms 口が開きっぱなしになる。
        ///   <c>-∞</c> で初期化してはいけないのと同じ根。
        /// </summary>
        [Test]
        public void SpanIsCappedAfterALongGap()
        {
            var tracker = new MouthTracker();
            tracker.Tick(0f, 100.0, Gain, Release, 1f / 30f);

            // 10 秒ハングした後の復帰フレーム
            Assert.That(tracker.From(110.0), Is.EqualTo(110.0 - MouthTracker.MaxSpanSeconds).Within(1e-9));
        }

        /// <summary>通常のフレーム間隔では上限に触れない（区間が切られると読み飛ばす）。</summary>
        [Test]
        public void NormalFramesAreNotCapped()
        {
            var tracker = new MouthTracker();
            tracker.Tick(0f, 100.0, Gain, Release, 1f / 30f);

            Assert.That(tracker.From(100.0 + 1.0 / 30.0), Is.EqualTo(100.0).Within(1e-9));
        }

        /// <summary>★ 立ち上がりは鈍らせない。鈍らせると口の応答が音より遅れる。</summary>
        [Test]
        public void AttackIsImmediate()
        {
            var tracker = new MouthTracker();

            // 0.25 * 4 = 1.0
            Assert.That(tracker.Tick(0.25f, 100.0, Gain, Release, 0.033f), Is.EqualTo(1f).Within(1e-5f));
        }

        /// <summary>
        /// ★ <b>減衰は <c>deltaTime</c> に比例させること。</b> 30fps と 60fps で
        /// 同じ壁時計時間だけ進めたら同じ値になる（<c>GazeAim.Smooth</c> と同じ規律）。
        /// </summary>
        [Test]
        public void ReleaseIsFrameRateIndependent()
        {
            var slow = new MouthTracker();
            var fast = new MouthTracker();

            slow.Tick(1f, 0.0, 1f, 0f, 0f);
            fast.Tick(1f, 0.0, 1f, 0f, 0f);
            Assert.That(slow.Weight, Is.EqualTo(1f).Within(1e-6f));

            // 0.1 秒ぶん進める
            for (var i = 0; i < 3; i++) slow.Tick(0f, 0.0, Gain, Release, 1f / 30f);
            for (var i = 0; i < 6; i++) fast.Tick(0f, 0.0, Gain, Release, 1f / 60f);

            Assert.That(slow.Weight, Is.EqualTo(0.2f).Within(1e-4f));
            Assert.That(fast.Weight, Is.EqualTo(slow.Weight).Within(1e-4f));
        }

        /// <summary>減衰を 0 にすると素通し（谷でそのまま閉じる）。</summary>
        [Test]
        public void ZeroReleaseFollowsTheInputDirectly()
        {
            var tracker = new MouthTracker();
            tracker.Tick(1f, 0.0, 1f, 0f, 1f / 30f);

            Assert.That(tracker.Tick(0f, 0.0, Gain, 0f, 1f / 30f), Is.EqualTo(0f));
        }

        [Test]
        public void ClampsIntoRange()
        {
            var tracker = new MouthTracker();

            Assert.That(tracker.Tick(1f, 0.0, 100f, Release, 0.033f), Is.EqualTo(1f));
            Assert.That(tracker.Tick(-1f, 0.0, Gain, 0f, 0.033f), Is.EqualTo(0f));
        }

        /// <summary>
        /// ★ <b>ゲインの 0 は「無効」ではなく「口が動かない」。</b> 他の調整値（<c>FaceParams</c>）が
        ///   「0 = 無効」で統一されているので取り違えやすい。だからゲインは
        ///   <see cref="FaceParams"/> ではなくここが持っている。
        /// </summary>
        [Test]
        public void ZeroGainKeepsTheMouthClosed()
        {
            var tracker = new MouthTracker();

            Assert.That(tracker.Tick(1f, 0.0, 0f, Release, 0.033f), Is.EqualTo(0f));
        }
    }
}
