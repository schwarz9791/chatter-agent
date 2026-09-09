using ChatterMascot.Protocol;
using ChatterMascot.Vrm;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="StallProbe"/>。ストール（<c>dt</c> / <c>gap</c>）、hips / head の飛びの判定、
    /// <c>lastMotionEvent</c> / <c>motionEdge</c> のフレーム数の計算を固定する（#103）。
    /// </summary>
    [TestFixture]
    public sealed class StallProbeTests
    {
        private static StallSample Sample(int frame, double now, float dt, float udt, Vector3? hips = null,
            Vector3? head = null, string motionState = "Idle", string motionEvent = null, bool speaking = false,
            SpeechKind kind = SpeechKind.Assistant)
        {
            return new StallSample
            {
                Frame = frame,
                Now = now,
                DeltaTime = dt,
                UnscaledDeltaTime = udt,
                HipsWorld = hips,
                HeadWorld = head,
                MotionState = motionState,
                MotionEvent = motionEvent,
                Speaking = speaking,
                Kind = kind,
            };
        }

        [Test]
        public void ReturnsNothingWhenEverythingIsBelowThreshold()
        {
            var probe = new StallProbe();

            Assert.That(probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, Vector3.zero)), Is.Empty);
            Assert.That(probe.Observe(Sample(2, 0.033, 0.033f, 0.033f, new Vector3(0.01f, 0f, 0f))), Is.Empty);
        }

        /// <summary>★ <c>DeltaTime</c> 単独でも立つ。<c>gap</c> が正常でも見逃さない。</summary>
        [Test]
        public void DeltaTimeAloneTriggersStall()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f));

            var lines = probe.Observe(Sample(2, 0.033, 0.333f, 0.333f, speaking: true, kind: SpeechKind.Assistant));

            Assert.That(lines.Count, Is.EqualTo(1));
            StringAssert.Contains("[Mascot] stall:", lines[0]);
            StringAssert.Contains("dt=0.333", lines[0]);
            StringAssert.Contains("gap=0.033", lines[0]);
            StringAssert.Contains("speaking=true", lines[0]);
            StringAssert.Contains("kind=assistant", lines[0]);
            // 前フレームの hips / head が無いので n/a
            StringAssert.Contains("hips=n/a", lines[0]);
            StringAssert.Contains("head=n/a", lines[0]);
        }

        /// <summary>
        /// ★ <c>gap</c> 単独でも立つ（<c>dt</c> はクランプ済みで正常値のまま）。
        ///   <c>deltaTime</c> は 0.333 で頭打ちなので、真のストール長は <c>gap</c> でしか見えない。
        /// </summary>
        [Test]
        public void GapAloneTriggersStall()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f));

            var lines = probe.Observe(Sample(2, 0.8, 0.033f, 0.8f, kind: SpeechKind.Prompt));

            Assert.That(lines.Count, Is.EqualTo(1));
            StringAssert.Contains("dt=0.033", lines[0]);
            StringAssert.Contains("gap=0.800", lines[0]);
            StringAssert.Contains("kind=prompt", lines[0]);
        }

        /// <summary>★ 前フレームの hips が無い初回は判定しない。</summary>
        [Test]
        public void HipsJumpDoesNotFireOnTheFirstFrame()
        {
            var probe = new StallProbe();
            var lines = probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, new Vector3(10f, 10f, 10f)));

            Assert.That(lines, Is.Empty);
        }

        [Test]
        public void HipsJumpFiresWhenTheRealBoneMoves()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, Vector3.zero));

            var lines = probe.Observe(Sample(2, 0.033, 0.033f, 0.033f, new Vector3(0.31f, 0f, 0f)));

            Assert.That(lines.Count, Is.EqualTo(1));
            StringAssert.Contains("[Mascot] hipsJump:", lines[0]);
            StringAssert.Contains("moved=0.310m", lines[0]);
        }

        /// <summary>★ <c>head=</c> の移動量が <c>stall:</c> 行に載る。前フレームが無ければ n/a。</summary>
        [Test]
        public void StallLineReportsHowFarTheHeadMoved()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, head: Vector3.zero));

            var lines = probe.Observe(Sample(2, 0.4, 0.4f, 0.4f, head: new Vector3(0.05f, 0f, 0f)));

            Assert.That(lines.Count, Is.EqualTo(1));
            StringAssert.Contains("[Mascot] stall:", lines[0]);
            StringAssert.Contains("head=0.050m", lines[0]);
        }

        /// <summary>frame N のモーションイベントは、frame N+1 のストール行に「1 フレーム前」として載る。</summary>
        [Test]
        public void LastMotionEventReportsAgeInFrames()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(5, 0.0, 0.033f, 0.033f,
                motionState: "FadeIn(happy_01.vrma)", motionEvent: "Play:Emotion:happy_01.vrma"));

            var lines = probe.Observe(Sample(6, 0.4, 0.033f, 0.4f, motionState: "FadeIn(happy_01.vrma)"));

            // ★ frame 6 は age=1 で motionEdge の窓にも入るので、stall: と motionEdge: の2行になる
            Assert.That(lines.Count, Is.EqualTo(2));
            StringAssert.Contains("[Mascot] stall:", lines[0]);
            StringAssert.Contains("lastMotionEvent=1(Play:Emotion:happy_01.vrma)", lines[0]);
            StringAssert.Contains("[Mascot] motionEdge:", lines[1]);
        }

        /// <summary>モーションイベントを一度も渡していなければ <c>none</c>。</summary>
        [Test]
        public void LastMotionEventIsNoneUntilOneArrives()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f));

            var lines = probe.Observe(Sample(2, 0.5, 0.5f, 0.5f));

            Assert.That(lines.Count, Is.EqualTo(1));
            StringAssert.Contains("lastMotionEvent=none", lines[0]);
        }

        /// <summary>★ 1行にまとめない。grep で stall と hipsJump を別に数えられるようにする。</summary>
        [Test]
        public void StallAndHipsJumpTogetherProduceTwoLines()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, Vector3.zero));

            var lines = probe.Observe(Sample(2, 0.4, 0.4f, 0.4f, new Vector3(0.5f, 0f, 0f)));

            Assert.That(lines.Count, Is.EqualTo(2));
            StringAssert.Contains("[Mascot] stall:", lines[0]);
            StringAssert.Contains("[Mascot] hipsJump:", lines[1]);
        }

        // ---- motionEdge（閾値未満でも遷移直後を無条件に見る、#103 追加） ----

        /// <summary>
        /// イベントのフレームから <see cref="StallProbe.MotionEdgeFrames"/> 分だけ無条件に出て、
        /// その次のフレームでは（閾値未満なら）何も出ない。
        /// </summary>
        [Test]
        public void MotionEdgeCoversTheEventFrameAndTheFollowingWindow()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f));

            var frame2 = probe.Observe(Sample(2, 0.033, 0.033f, 0.033f, motionEvent: "Play:Emotion:x.vrma"));
            Assert.That(frame2.Count, Is.EqualTo(1));
            StringAssert.Contains("[Mascot] motionEdge:", frame2[0]);
            StringAssert.Contains("age=0", frame2[0]);
            StringAssert.Contains("event=Play:Emotion:x.vrma", frame2[0]);

            var frame3 = probe.Observe(Sample(3, 0.066, 0.033f, 0.033f));
            Assert.That(frame3.Count, Is.EqualTo(1));
            StringAssert.Contains("age=1", frame3[0]);
            StringAssert.Contains("event=-", frame3[0]);

            var frame4 = probe.Observe(Sample(4, 0.099, 0.033f, 0.033f));
            Assert.That(frame4.Count, Is.EqualTo(1));
            StringAssert.Contains("age=2", frame4[0]);

            var frame5 = probe.Observe(Sample(5, 0.132, 0.033f, 0.033f));
            Assert.That(frame5.Count, Is.EqualTo(1));
            StringAssert.Contains("age=3", frame5[0]);

            // ★ MotionEdgeFrames=3 なので age=0..3 の4フレームだけ。ここでは窓の外
            var frame6 = probe.Observe(Sample(6, 0.165, 0.033f, 0.033f));
            Assert.That(frame6, Is.Empty);
        }

        /// <summary>窓の途中で新しいイベントが来たら age を 0 から数え直す。</summary>
        [Test]
        public void MotionEdgeWindowRestartsOnANewEventMidway()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, motionEvent: "Play:Emotion:a.vrma"));

            var frame2 = probe.Observe(Sample(2, 0.033, 0.033f, 0.033f));
            Assert.That(frame2.Count, Is.EqualTo(1));
            StringAssert.Contains("age=1", frame2[0]);

            var frame3 = probe.Observe(Sample(3, 0.066, 0.033f, 0.033f, motionEvent: "Play:Accent:b.vrma"));
            Assert.That(frame3.Count, Is.EqualTo(1));
            StringAssert.Contains("age=0", frame3[0]);
            StringAssert.Contains("event=Play:Accent:b.vrma", frame3[0]);

            var frame4 = probe.Observe(Sample(4, 0.099, 0.033f, 0.033f));
            Assert.That(frame4.Count, Is.EqualTo(1));
            StringAssert.Contains("age=1", frame4[0]);
        }

        /// <summary>
        /// ★ hips / head が 0.3m 未満しか動いていなくても（<c>hipsJump:</c> は出ない）、
        ///   遷移直後は移動量が <c>motionEdge:</c> 行に載る。
        /// </summary>
        [Test]
        public void MotionEdgeReportsSmallMovesThatHipsJumpWouldMiss()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, Vector3.zero, Vector3.zero,
                motionEvent: "Play:Emotion:x.vrma"));

            var lines = probe.Observe(Sample(2, 0.033, 0.033f, 0.033f,
                new Vector3(0.05f, 0f, 0f), new Vector3(0.02f, 0f, 0f)));

            Assert.That(lines.Count, Is.EqualTo(1));
            StringAssert.Contains("[Mascot] motionEdge:", lines[0]);
            StringAssert.Contains("hips=0.050m", lines[0]);
            StringAssert.Contains("head=0.020m", lines[0]);
        }

        // ---- nanPose（位置の NaN は Unity が警告しない、#103 追加） ----

        /// <summary>★ 窓や閾値に関係なく無条件——最初のフレームでも出る。</summary>
        [Test]
        public void NanPoseFiresWhenHipsIsNaN()
        {
            var probe = new StallProbe();
            var lines = probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, new Vector3(float.NaN, 0f, 0f)));

            Assert.That(lines.Count, Is.EqualTo(1));
            StringAssert.Contains("[Mascot] nanPose:", lines[0]);
            StringAssert.Contains("hips=nan", lines[0]);
            // head は渡していない（null）ので nan ではない
            StringAssert.Contains("head=ok", lines[0]);
        }

        /// <summary>通常の位置では、他の行（ここでは stall:）が立っても nanPose: は出ない。</summary>
        [Test]
        public void NanPoseDoesNotFireForOrdinaryPositions()
        {
            var probe = new StallProbe();
            var lines = probe.Observe(Sample(1, 0.0, 0.4f, 0.4f, Vector3.zero, Vector3.zero));

            Assert.That(lines.Count, Is.EqualTo(1));
            StringAssert.Contains("[Mascot] stall:", lines[0]);
        }

        /// <summary>4行すべてが同じフレームで重なったときの順序: stall → hipsJump → nanPose → motionEdge。</summary>
        [Test]
        public void LinesAppearInAFixedOrderWhenTheyOverlap()
        {
            var probe = new StallProbe();
            probe.Observe(Sample(1, 0.0, 0.033f, 0.033f, Vector3.zero, Vector3.zero));

            var lines = probe.Observe(Sample(2, 0.4, 0.4f, 0.4f,
                new Vector3(0.5f, 0f, 0f), new Vector3(float.NaN, 0f, 0f),
                motionEvent: "Play:Emotion:x.vrma"));

            Assert.That(lines.Count, Is.EqualTo(4));
            StringAssert.Contains("[Mascot] stall:", lines[0]);
            StringAssert.Contains("[Mascot] hipsJump:", lines[1]);
            StringAssert.Contains("[Mascot] nanPose:", lines[2]);
            StringAssert.Contains("[Mascot] motionEdge:", lines[3]);
        }
    }
}
