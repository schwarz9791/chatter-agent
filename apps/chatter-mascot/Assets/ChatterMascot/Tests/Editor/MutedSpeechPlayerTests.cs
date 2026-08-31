using System;
using System.Collections;
using System.Threading.Tasks;
using ChatterMascot.Audio;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ChatterMascot.Tests
{
    [TestFixture]
    public sealed class MutedSpeechPlayerTests
    {
        private sealed class FakeHandle : IAudioDuration
        {
            public int DurationMs { get; set; }
        }

        private sealed class FakePlayer : ISpeechPlayer
        {
            public int PlayCalls;
            public int StopAllCalls;
            public int DiscardCalls;
            public string PlayResult;

            /// <summary>非 null ならこれを返す（完了のタイミングをテストから決める）</summary>
            public TaskCompletionSource<string> Gate;
            public int Active;
            public bool Suspendable = true;

            // ★ 使わないが、デコレータが素通しすることを型で示すために持つ
            public event Action<string> Warn;

            public void RaiseWarn(string message)
            {
                var warn = Warn;
                if (warn != null) warn(message);
            }

            public object Prepare(byte[] wav, string name, out string error)
            {
                error = null;
                return new FakeHandle { DurationMs = 100 };
            }

            public Task<string> PlayAsync(object audio)
            {
                PlayCalls++;
                if (Gate != null) return Gate.Task;
                return Task.FromResult(PlayResult);
            }

            public void Discard(object audio)
            {
                DiscardCalls++;
            }

            public void StopAll()
            {
                StopAllCalls++;
            }

            public int ActiveCount
            {
                get { return Active; }
            }

            public bool CanSuspendOutput
            {
                get { return Suspendable; }
            }

            public void SuspendOutput()
            {
            }

            public void ResumeOutput()
            {
            }
        }

        private FakePlayer _inner;
        private MuteState _mute;
        private MutedSpeechPlayer _player;

        [SetUp]
        public void SetUp()
        {
            _inner = new FakePlayer();
            _mute = new MuteState();
            _player = new MutedSpeechPlayer(_inner, _mute);
        }

        [TearDown]
        public void TearDown()
        {
            _player.Dispose();
        }

        // ---- ミュートしていないとき ----

        [UnityTest]
        public IEnumerator PassesThroughWhenNotMuted()
        {
            var task = _player.PlayAsync(new FakeHandle { DurationMs = 100 });
            while (!task.IsCompleted) yield return null;

            Assert.That(_inner.PlayCalls, Is.EqualTo(1));
            Assert.That(task.Result, Is.Null);
        }

        [UnityTest]
        public IEnumerator ReportsFailuresFromTheInnerPlayer()
        {
            _inner.PlayResult = "壊れた WAV";

            var task = _player.PlayAsync(new FakeHandle());
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.EqualTo("壊れた WAV"), "失敗を握り潰さない");
        }

        // ---- ミュートしているとき ----

        /// <summary>
        /// ★★ <b>ミュート中も成功を返すこと。</b> 返さないと <c>ack</c> が出ず、
        /// キューが上限（500）まで溜まって古い方から捨てられる
        /// （→ <c>docs/protocol.md</c> の責務2）。
        /// </summary>
        [UnityTest]
        public IEnumerator SucceedsWithoutPlayingWhenMuted()
        {
            _mute.Muted = true;

            var task = _player.PlayAsync(new FakeHandle { DurationMs = 60 });
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Null, "成功として返す（ack が出る）");
            Assert.That(_inner.PlayCalls, Is.EqualTo(0), "音は出さない");
        }

        /// <summary>
        /// ★★ <b>長さぶん待つこと。</b> 即座に返すと、溜まっていた発話が数百 ms で全部
        /// 消化されて<b>表情が高速で切り替わる</b>。「声だけ消す」は実時間を消費して成立する。
        /// </summary>
        [UnityTest]
        public IEnumerator WaitsForTheDurationWhenMuted()
        {
            _mute.Muted = true;

            var startedAt = Time.realtimeSinceStartupAsDouble;
            var task = _player.PlayAsync(new FakeHandle { DurationMs = 250 });

            Assert.That(task.IsCompleted, Is.False, "その場では終わらない");

            while (!task.IsCompleted) yield return null;

            // ★ 上限は見ない。Editor のフレーム間隔と Task.Delay の精度に依存する
            Assert.That(Time.realtimeSinceStartupAsDouble - startedAt, Is.GreaterThan(0.15));
        }

        /// <summary>0 は「長さ 0」ではなく「不明」。待つ根拠が無い。</summary>
        [UnityTest]
        public IEnumerator DoesNotWaitWhenTheLengthIsUnknown()
        {
            _mute.Muted = true;

            var task = _player.PlayAsync(new FakeHandle { DurationMs = 0 });
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Null);
        }

        /// <summary>長さを持たないハンドル（別実装）でも落ちない。</summary>
        [UnityTest]
        public IEnumerator ToleratesAHandleWithoutADuration()
        {
            _mute.Muted = true;

            var task = _player.PlayAsync(new object());
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Null);
        }

        /// <summary>
        /// ★★ <b>待っている本数を <c>ActiveCount</c> に数えること。</b> 数えないと
        /// <c>AudioIdleGate</c> が「鳴っていない」と判定して出力デバイスを手放す
        /// （macOS は <c>CanSuspendOutput == false</c> で無害だが、Android では効く → #25）。
        /// </summary>
        [UnityTest]
        public IEnumerator CountsTheSilentWaitAsActive()
        {
            _mute.Muted = true;

            var task = _player.PlayAsync(new FakeHandle { DurationMs = 250 });
            Assert.That(_player.ActiveCount, Is.EqualTo(1));

            while (!task.IsCompleted) yield return null;

            Assert.That(_player.ActiveCount, Is.EqualTo(0), "終わったら外れる");
        }

        [Test]
        public void AddsTheInnerActiveCount()
        {
            _inner.Active = 3;
            Assert.That(_player.ActiveCount, Is.EqualTo(3));
        }

        /// <summary>★ 待機を切っても失敗にしないこと（ack が出なくなる）。</summary>
        [UnityTest]
        public IEnumerator StopAllEndsTheSilentWaitAsSuccess()
        {
            _mute.Muted = true;

            var task = _player.PlayAsync(new FakeHandle { DurationMs = 10000 });
            Assert.That(task.IsCompleted, Is.False);

            _player.StopAll();
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Null);
            Assert.That(_inner.StopAllCalls, Is.GreaterThanOrEqualTo(1));
        }

        // ---- 切り替えたとき ----

        /// <summary>
        /// ★ <b>押した瞬間に黙ること。</b> 「いま喋っているのを止めたい」が押す動機なので、
        /// 次の発話から効くのでは遅い。
        /// </summary>
        [Test]
        public void StopsWhatIsPlayingWhenMuteIsTurnedOn()
        {
            _mute.Muted = true;
            Assert.That(_inner.StopAllCalls, Is.EqualTo(1));
        }

        [Test]
        public void DoesNotStopAnythingWhenMuteIsTurnedOff()
        {
            _mute.Muted = true;
            _inner.StopAllCalls = 0;

            _mute.Muted = false;
            Assert.That(_inner.StopAllCalls, Is.EqualTo(0));
        }

        /// <summary>
        /// 再生中にミュートされたら、止めたのは自分。★ 失敗として数えると
        /// 押した本人に向かって警告が出る。
        /// </summary>
        [UnityTest]
        public IEnumerator DoesNotReportAFailureCausedByMuting()
        {
            // 入口は非ミュート → 鳴っている最中にミュートされる → 内側が失敗を返す、という順を作る
            var gate = new TaskCompletionSource<string>();
            _inner.Gate = gate;

            var task = _player.PlayAsync(new FakeHandle());
            Assert.That(_inner.PlayCalls, Is.EqualTo(1), "非ミュートなので本物に流れる");

            _mute.Muted = true;
            gate.SetResult("止められました");

            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Null, "自分が止めたものを失敗として数えない");
        }

        /// <summary>ミュートと無関係な失敗は、ミュート中でもそのまま返す…わけではない点に注意。</summary>
        [UnityTest]
        public IEnumerator KeepsReportingFailuresWhileNotMuted()
        {
            var gate = new TaskCompletionSource<string>();
            _inner.Gate = gate;

            var task = _player.PlayAsync(new FakeHandle());
            gate.SetResult("WAV がありません");

            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.EqualTo("WAV がありません"));
        }

        // ---- 素通し ----

        [Test]
        public void DelegatesEverythingElse()
        {
            _inner.Suspendable = false;
            Assert.That(_player.CanSuspendOutput, Is.False);

            string error;
            Assert.That(_player.Prepare(new byte[0], "speech-1-1", out error), Is.Not.Null);

            _player.Discard(new object());
            Assert.That(_inner.DiscardCalls, Is.EqualTo(1));
        }

        /// <summary>診断を握り潰さない（無音の原因を追う唯一の窓）。</summary>
        [Test]
        public void ForwardsWarnings()
        {
            string received = null;
            Action<string> handler = message => received = message;

            _player.Warn += handler;
            _inner.RaiseWarn("困った");
            Assert.That(received, Is.EqualTo("困った"));

            _player.Warn -= handler;
            received = null;
            _inner.RaiseWarn("もう届かない");
            Assert.That(received, Is.Null);
        }

        /// <summary>★ 購読を外すこと（Desktop 側は DontDestroyOnLoad で生き続ける）。</summary>
        [Test]
        public void DisposeUnsubscribesFromTheMuteState()
        {
            _player.Dispose();

            _mute.Muted = true;
            Assert.That(_inner.StopAllCalls, Is.EqualTo(0));
        }
    }
}
