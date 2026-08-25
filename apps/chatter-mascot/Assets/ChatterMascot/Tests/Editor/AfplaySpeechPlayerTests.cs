#if UNITY_EDITOR_OSX
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ChatterMascot.Audio;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// macOS の<b>本番の再生経路</b>。
    ///
    /// ★ <c>command</c> を差し替えられるので、**オーディオデバイス無しで回せる**
    ///   （参照実装が <c>docs/core.md</c> で同じ発想を使っている: 偽プレイヤーに差し替えて
    ///   「何がどの順で鳴ったか」まで CI で検証する）。
    ///
    /// ★ <c>#if UNITY_EDITOR_OSX</c> で囲んであるのは、#54 で CI が Linux で回る可能性があるため。
    /// </summary>
    [TestFixture]
    public sealed class AfplaySpeechPlayerTests
    {
        /// <summary>16bit PCM の最小 WAV。24000Hz / 1ch なので 2400 サンプル = 100ms。</summary>
        private static byte[] BuildWav(int sampleCount)
        {
            const int sampleRate = 24000;
            const ushort channels = 1;
            var dataBytes = sampleCount * 2;
            var bytes = new List<byte>();

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bytes.AddRange(BitConverter.GetBytes(36 + dataBytes));
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bytes.AddRange(BitConverter.GetBytes(16));
            bytes.AddRange(BitConverter.GetBytes((ushort)1));
            bytes.AddRange(BitConverter.GetBytes(channels));
            bytes.AddRange(BitConverter.GetBytes(sampleRate));
            bytes.AddRange(BitConverter.GetBytes(sampleRate * channels * 2));
            bytes.AddRange(BitConverter.GetBytes((ushort)(channels * 2)));
            bytes.AddRange(BitConverter.GetBytes((ushort)16));

            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
            bytes.AddRange(BitConverter.GetBytes(dataBytes));
            bytes.AddRange(new byte[dataBytes]);

            return bytes.ToArray();
        }

        // ---- 期限（純粋） ----

        [Test]
        public void TimeoutIsProportionalToLength()
        {
            // 実長 × 2 + 5秒（→ docs/mascot.md）
            Assert.That(AfplaySpeechPlayer.TimeoutSecondsFor(1000), Is.EqualTo(7f).Within(0.01f));
            Assert.That(AfplaySpeechPlayer.TimeoutSecondsFor(20000), Is.EqualTo(45f).Within(0.01f));
        }

        /// <summary>
        /// ★ <b>0 は「長さ 0」ではなく「不明」。</b> 長さ 0 として `0 * 2 + 5秒` を計算すると
        /// <b>すべての再生が5秒で打ち切られ</b>、切られた文は PlaybackFailed → ack に落ちて
        /// サーバーのキューからも消える（二度と鳴らせない）。
        /// </summary>
        [Test]
        public void UnknownLengthFallsBackToTheLongTimeout()
        {
            Assert.That(AfplaySpeechPlayer.TimeoutSecondsFor(0), Is.EqualTo(120f).Within(0.01f));
            Assert.That(AfplaySpeechPlayer.TimeoutSecondsFor(-1), Is.EqualTo(120f).Within(0.01f));
        }

        // ---- Prepare / Discard ----

        [Test]
        public void PrepareWritesTheWavAndReadsItsLength()
        {
            var player = new AfplaySpeechPlayer("/usr/bin/true");
            string error;
            var handle = player.Prepare(BuildWav(2400), "speech-1-000000000042", out error) as AfplayAudioHandle;

            Assert.That(error, Is.Null);
            Assert.That(handle, Is.Not.Null);
            Assert.That(handle.DurationMs, Is.EqualTo(100));
            Assert.That(File.Exists(handle.Path), Is.True);
            // ★ ファイル名に epoch が入っていること。seq だけだと採番のやり直しで同じ名前が戻り、
            //   孤児の afplay が読んでいる最中のファイルを truncate する
            Assert.That(Path.GetFileName(handle.Path), Is.EqualTo("speech-1-000000000042.wav"));

            player.Discard(handle);
        }

        [Test]
        public void PrepareRejectsBrokenWavWithAReason()
        {
            var player = new AfplaySpeechPlayer("/usr/bin/true");
            string error;
            var handle = player.Prepare(new byte[] { 1, 2, 3 }, "speech-1-000000000001", out error);

            Assert.That(handle, Is.Null);
            // ★ 理由が残ること。無音の原因を診断する窓
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void DiscardRemovesTheFileAndIsSafeToRepeat()
        {
            var player = new AfplaySpeechPlayer("/usr/bin/true");
            string error;
            var handle = player.Prepare(BuildWav(240), "speech-1-000000000002", out error) as AfplayAudioHandle;
            var path = handle.Path;

            player.Discard(handle);
            Assert.That(File.Exists(path), Is.False);
            Assert.That(handle.Path, Is.Null);

            // 2回目・null・別の型でも throw しない
            Assert.DoesNotThrow(() => player.Discard(handle));
            Assert.DoesNotThrow(() => player.Discard(null));
            Assert.DoesNotThrow(() => player.Discard("not a handle"));
        }

        /// <summary>
        /// ★ 一時ディレクトリが消えても復帰できること。macOS はディスク逼迫で
        /// <c>~/Library/Caches</c> をパージするので、実行中に消えうる。
        /// </summary>
        [Test]
        public void PrepareRecreatesTheDirectoryIfItDisappears()
        {
            var player = new AfplaySpeechPlayer("/usr/bin/true");
            string error;
            var first = player.Prepare(BuildWav(240), "speech-1-000000000003", out error) as AfplayAudioHandle;
            var dir = Path.GetDirectoryName(first.Path);

            Directory.Delete(dir, true);
            Assert.That(Directory.Exists(dir), Is.False);

            var second = player.Prepare(BuildWav(240), "speech-1-000000000004", out error) as AfplayAudioHandle;
            Assert.That(error, Is.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(File.Exists(second.Path), Is.True);
        }

        [Test]
        public void StartsIdle()
        {
            var player = new AfplaySpeechPlayer("/usr/bin/true");
            Assert.That(player.ActiveCount, Is.EqualTo(0));
            // ★ 1発話 = 1プロセスなので手放すものが無い（→ アイドル判定ごと止まる）
            Assert.That(player.CanSuspendOutput, Is.False);
        }

        // ---- プロセスの再生ループ ----

        [UnityTest]
        public IEnumerator PlaysWithAStubCommand()
        {
            var player = new AfplaySpeechPlayer("/usr/bin/true");
            string error;
            var handle = player.Prepare(BuildWav(240), "speech-1-000000000010", out error);

            var task = player.PlayAsync(handle);
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Null, "成功なら null を返す");
            Assert.That(player.ActiveCount, Is.EqualTo(0), "終わったら記帳から外れる");
            player.Discard(handle);
        }

        /// <summary>失敗も「喋り終えた」と同じ経路に落とすため、理由を返して例外は投げない。</summary>
        [UnityTest]
        public IEnumerator ReportsFailureWhenTheCommandExitsNonZero()
        {
            var player = new AfplaySpeechPlayer("/usr/bin/false");
            string error;
            var handle = player.Prepare(BuildWav(240), "speech-1-000000000011", out error);

            var task = player.PlayAsync(handle);
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Not.Null.And.Not.Empty);
            Assert.That(player.ActiveCount, Is.EqualTo(0));
            player.Discard(handle);
        }

        [UnityTest]
        public IEnumerator ReportsFailureWhenTheCommandIsMissing()
        {
            var player = new AfplaySpeechPlayer("/nonexistent/player");
            string error;
            var handle = player.Prepare(BuildWav(240), "speech-1-000000000012", out error);

            var task = player.PlayAsync(handle);
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Not.Null.And.Not.Empty);
            Assert.That(player.ActiveCount, Is.EqualTo(0), "起動に失敗したものを記帳に残さない");
            player.Discard(handle);
        }

        /// <summary>
        /// 異常終了（引数を解釈できない等）でも<b>必ず戻ってくる</b>こと。
        ///
        /// ★ <b>戻ってこない再生は head-of-line blocking になり、1回のハングで以後すべてが
        ///   無音になる</b>（参照実装のコメント: 「Bluetooth ヘッドフォンが再生中に切れると
        ///   afplay は戻ってこないことがある」）。
        ///
        /// ★ <b>タイムアウトそのものは、ここでは踏めない。</b> この実装はコマンドに WAV の
        ///   パスしか渡さない（固定引数を渡す口が無い）ので、テストから「居座るプロセス」を
        ///   作れない。期限の計算自体は <see cref="TimeoutIsProportionalToLength"/> と
        ///   <see cref="UnknownLengthFallsBackToTheLongTimeout"/> で純粋に固定してある。
        /// </summary>
        [UnityTest]
        public IEnumerator ReturnsEvenWhenTheCommandCannotHandleTheArgument()
        {
            // sleep は WAV のパスを秒数として解釈できずエラー終了する
            var player = new AfplaySpeechPlayer("/bin/sleep");
            string error;
            var handle = player.Prepare(BuildWav(240), "speech-1-000000000013", out error);

            var task = player.PlayAsync(handle);
            while (!task.IsCompleted) yield return null;

            Assert.That(task.Result, Is.Not.Null, "失敗の理由を返す（例外は投げない）");
            Assert.That(player.ActiveCount, Is.EqualTo(0), "終わったら記帳から外れる");
            player.Discard(handle);
        }

        /// <summary>
        /// <c>StopAll</c> のあとに記帳が空になること。
        ///
        /// ★ <c>ActiveCount</c> は<b>アイドル判定が契約の防衛線として信頼している値</b>
        ///   （<c>AudioIdleGate</c> は 0 でなければ絶対に手放さない）。ここが実態から
        ///   ずれると、孤児が鳴っている最中にデバイスを手放して<b>その音が凍る</b>。
        ///
        /// ★ 「再生中は 1 である」ことは<b>ここでは固定できない</b>。スタブのコマンドは
        ///   即座に終わるので、記帳に載っている時間が1フレームより短いことがある。
        /// </summary>
        [UnityTest]
        public IEnumerator StopAllEmptiesTheRegistry()
        {
            var player = new AfplaySpeechPlayer("/usr/bin/true");
            string error;
            var handle = player.Prepare(BuildWav(240), "speech-1-000000000014", out error);

            var task = player.PlayAsync(handle);
            player.StopAll();
            while (!task.IsCompleted) yield return null;

            Assert.That(player.ActiveCount, Is.EqualTo(0));
            player.Discard(handle);
        }
    }
}
#endif
