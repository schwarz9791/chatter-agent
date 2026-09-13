using ChatterMascot.Audio;
using NUnit.Framework;
using UnityEngine;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="AudioClipPlayer.Prepare"/>。Android / iOS の実体（→ <c>SpeechPlayerFactory</c>）。
    ///
    /// ★ <b><c>#if</c> で囲まないこと。</b> <c>AudioClipPlayer</c> は macOS の Editor / Play Mode
    ///   でも生成できる実装で、プラットフォーム非依存にテストできる
    ///   （→ <c>AfplaySpeechPlayerTests</c> と違い、外部プロセスを呼ばない）。
    /// </summary>
    [TestFixture]
    public sealed class AudioClipPlayerTests
    {
        private GameObject _templateObject;
        private AudioClipPlayer _player;

        [SetUp]
        public void SetUp()
        {
            _templateObject = new GameObject("AudioClipPlayerTests.Template");
            var template = _templateObject.AddComponent<AudioSource>();
            _player = new AudioClipPlayer(template);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_templateObject);
        }

        [Test]
        public void PrepareReturnsAHandleForAValidWav()
        {
            // 24000Hz / 1ch / 16bit で 2400 サンプル = ちょうど 100ms（WavDecoderTests と同じ根拠）
            var wav = WavBuilder.Build(new short[2400]);

            string error;
            var handle = _player.Prepare(wav, "test", out error) as UnityAudioHandle;

            Assert.That(error, Is.Null);
            Assert.That(handle, Is.Not.Null);
            Assert.That(handle.Clip, Is.Not.Null);
            Assert.That(handle.DurationMs, Is.EqualTo(100));
            Assert.That(handle.Envelope, Is.Not.Null);
            Assert.That(handle.EnvelopeFrameMs, Is.EqualTo(LipSyncEnvelope.DefaultFrameMs));

            _player.Discard(handle);
        }

        [Test]
        public void PrepareOnGarbageBytesReturnsNullWithAReason()
        {
            string error;
            var handle = _player.Prepare(new byte[] { 1, 2, 3, 4 }, "test", out error);

            Assert.That(handle, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        // ---- 「エンベロープは作れないが Prepare は成功する」経路（skip の理由） ----
        //
        // ★ LipSyncEnvelopeTests.UnsupportedBitDepthReturnsNull が使う 12bit PCM は、
        //   WavDecoder.Decode 側も同じ WavDecoder.BytesPerSample で弾かれる。つまり
        //   AudioClip の生成そのものが失敗し、AudioClipPlayer.Prepare はエンベロープを
        //   組む前に null を返す —— 「AudioClip は作れるのにエンベロープだけ作れない」
        //   入力を WavBuilder で作る方法は今のところ無い（Decode とエンベロープが
        //   同じ BytesPerSample を見ているので、対応ビット深度が両者で割れることがない）。
        //   この経路（Envelope == null でも Prepare 自体は成功する契約）を守るテストは、
        //   ここではなく LipSyncEnvelope.BuildOrWarn* がプラットフォーム非依存に踏んでいる
        //   （→ LipSyncEnvelope のクラス doc）。
    }
}
