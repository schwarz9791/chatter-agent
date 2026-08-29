using ChatterMascot.Audio;
using ChatterMascot.Protocol;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="SpeakingSet"/>。いま鳴っている発話の集合から、口の開きと表情を出す。
    ///
    /// ★★ <b>これが <c>SpeakingView</c> を置き換えた（#58）。</b> あちらは
    ///   <c>PlaybackState.Items</c> を走査していたので、採番のやり直しで <c>Orphans</c> へ
    ///   移った孤児（<c>Record</c> を持たない）を見られず、<b>鳴っているのに「喋っていない」</b>
    ///   と答えていた。ここは<b>再生を始めた時点で写し取る</b>ので、その穴が閉じている。
    /// </summary>
    [TestFixture]
    public sealed class SpeakingSetTests
    {
        private const int FrameMs = 20;

        private static SpeakingSet WithOne(float[] envelope, double startedAt = 0.0)
        {
            var set = new SpeakingSet();
            set.Begin(1, 1, Emotion.Neutral, SpeechKind.Assistant, envelope, FrameMs, startedAt);
            return set;
        }

        // ---- 口の開き ----

        /// <summary>
        /// ★ <b>孤児が重なっている間は全発話の <c>max</c>。</b> 口は1つでスピーカーも1つなので、
        ///   「今いちばん大きく鳴っている音」に合わせるのが物理的に正しい。
        ///   先に始まったほうだけを見ると、重なっている間に口が止まる。
        /// </summary>
        [Test]
        public void OverlappingSpeechTakesTheMaximum()
        {
            var set = new SpeakingSet();
            set.Begin(1, 1, Emotion.Neutral, SpeechKind.Assistant, new[] { 0.3f, 0.3f }, FrameMs, 0.0);
            set.Begin(2, 1, Emotion.Neutral, SpeechKind.Assistant, new[] { 0.9f, 0.9f }, FrameMs, 0.0);

            Assert.That(set.Count, Is.EqualTo(2));
            Assert.That(set.Mouth(0.0, 0.01, 0), Is.EqualTo(0.9f).Within(1e-6f));

            // 新しいほうが鳴り終わっても、孤児のぶんは残る
            set.End(2, 1);
            Assert.That(set.Mouth(0.0, 0.01, 0), Is.EqualTo(0.3f).Within(1e-6f));
        }

        [Test]
        public void EndRemovesTheEntry()
        {
            var set = WithOne(new[] { 1f, 1f });

            Assert.That(set.Count, Is.EqualTo(1));
            set.End(1, 1);

            Assert.That(set.Count, Is.EqualTo(0));
            Assert.That(set.Mouth(0.0, 0.01, 0), Is.EqualTo(0f));
        }

        /// <summary>
        /// ★★ <b>offset の索引で「負を 0 にクランプ」しないこと。</b>
        ///   <c>index = max(0, index)</c> と書くと、offset ぶんの先行区間で <c>envelope[0]</c> を
        ///   返す＝<b>音より先に口が動く</b>ので、offset を入れた意味が消える。
        /// </summary>
        [Test]
        public void OffsetKeepsTheMouthClosedBeforeTheSoundStarts()
        {
            var set = WithOne(new[] { 1f, 1f, 1f });

            // 100ms のラグ。区間全体がまだ音より前
            Assert.That(set.Mouth(0.0, 0.05, 100), Is.EqualTo(0f));

            // ラグを過ぎれば読める
            Assert.That(set.Mouth(0.1, 0.12, 100), Is.EqualTo(1f).Within(1e-6f));
        }

        /// <summary>
        /// ★★ <b>点ではなく区間で読むこと。</b> エンベロープの刻み（20ms）と表示（30fps = 33.3ms）は
        ///   割り切れないので、点サンプリングでは<b>読み飛ばされるフレームがある</b>（33.3ms 間隔だと
        ///   フレーム 2 / 4 / 7 / 9 … に一度も当たらない）。区間の最大なら必ず拾う。
        /// </summary>
        [Test]
        public void RangeSamplingBeatsPointSampling()
        {
            var envelope = new float[6];
            envelope[2] = 1f;   // 40〜60ms にだけ立っているスパイク
            var set = WithOne(envelope);

            // 30fps の点サンプリング（1/30 = 33.3ms 刻み）はこのスパイクをまたいで通り過ぎる
            Assert.That(set.Mouth(0.0333, 0.0333, 0), Is.EqualTo(0f));
            Assert.That(set.Mouth(0.0667, 0.0667, 0), Is.EqualTo(0f));

            // 同じ2点でも、区間として読めば拾える
            Assert.That(set.Mouth(0.0333, 0.0667, 0), Is.EqualTo(1f).Within(1e-6f));
        }

        [Test]
        public void ReturnsZeroPastTheEnd()
        {
            var set = WithOne(new[] { 1f, 1f, 1f });   // 0〜60ms

            Assert.That(set.Mouth(0.1, 0.12, 0), Is.EqualTo(0f));
        }

        /// <summary>
        /// ★ エンベロープが作れなかった発話（<c>null</c>）は口に寄与しないが、
        ///   <b>「喋っている」ことまで否定しない</b>。否定すると表情も体の動きも止まる。
        /// </summary>
        [Test]
        public void NullEnvelopeStillCountsAsSpeaking()
        {
            var set = new SpeakingSet();
            set.Begin(1, 1, Emotion.Happy, SpeechKind.Assistant, null, FrameMs, 0.0);

            Assert.That(set.Count, Is.EqualTo(1));
            Assert.That(set.Mouth(0.0, 0.01, 0), Is.EqualTo(0f));

            Emotion emotion;
            SpeechKind kind;
            Assert.That(set.TryGetFace(out emotion, out kind), Is.True);
            Assert.That(emotion, Is.EqualTo(Emotion.Happy));
        }

        [Test]
        public void ReversedOrEmptyRangeDoesNotThrow()
        {
            var set = WithOne(new[] { 1f, 1f });

            // 時計が巻き戻っても壊れない（realtimeSinceStartup では起きないはずだが）
            Assert.That(set.Mouth(0.02, 0.0, 0), Is.EqualTo(1f).Within(1e-6f));
            // 初回フレームの点サンプル
            Assert.That(set.Mouth(0.0, 0.0, 0), Is.EqualTo(1f).Within(1e-6f));
        }

        /// <summary>桁が離れた時刻で <c>(int)</c> の未定義値を踏まないこと。</summary>
        [Test]
        public void FarAwayTimesDoNotThrow()
        {
            var set = WithOne(new[] { 1f, 1f }, 1e9);

            Assert.That(set.Mouth(0.0, 0.01, 0), Is.EqualTo(0f));
            Assert.That(set.Mouth(1e12, 1e12 + 0.01, 0), Is.EqualTo(0f));
        }

        // ---- 表情 ----

        /// <summary>
        /// ★ <b>最後に始まったものを返す。</b> 表情は「今の話題」に従うべきで、
        ///   消えゆく旧エポック（孤児）ではない。
        /// </summary>
        [Test]
        public void TryGetFaceReturnsTheLatest()
        {
            var set = new SpeakingSet();
            set.Begin(1, 1, Emotion.Happy, SpeechKind.Assistant, null, FrameMs, 0.0);
            set.Begin(2, 1, Emotion.Sad, SpeechKind.Prompt, null, FrameMs, 1.0);

            Emotion emotion;
            SpeechKind kind;
            Assert.That(set.TryGetFace(out emotion, out kind), Is.True);
            Assert.That(emotion, Is.EqualTo(Emotion.Sad));
            Assert.That(kind, Is.EqualTo(SpeechKind.Prompt));

            // 新しいほうが終われば、まだ鳴っている孤児の表情に戻る
            set.End(2, 1);
            Assert.That(set.TryGetFace(out emotion, out kind), Is.True);
            Assert.That(emotion, Is.EqualTo(Emotion.Happy));
            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
        }

        /// <summary>
        /// ★★ <b><c>SpeakingView</c> から移送した契約。</b> <c>false</c> のときは
        ///   <c>Assistant</c> / <c>Neutral</c> に倒す。<c>VrmCharacter.LateUpdate</c> が
        ///   これに寄りかかっていて、呼び出し側で <c>Speaking ? kind : 既定</c> と書き直していない。
        /// </summary>
        [Test]
        public void TryGetFaceFallsBackToAssistantNeutral()
        {
            var set = new SpeakingSet();

            Emotion emotion;
            SpeechKind kind;
            Assert.That(set.TryGetFace(out emotion, out kind), Is.False);
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
        }

        /// <summary>
        /// ★★ <b>これが #58 の眼目。</b> 以前の <c>SpeakingView</c> は、採番のやり直しで
        ///   <c>Orphans</c> へ移った発話が鳴っている間 <b><c>false</c> を返していた</b>
        ///   （<c>Orphans</c> は音声ハンドルしか持たず <c>Record</c> を持たないため）。
        ///   ここでは <c>Begin</c> の時点で写し取っているので、<c>Items</c> から消えても答えられる。
        /// </summary>
        [Test]
        public void OrphansKeepSpeakingTrue()
        {
            var set = new SpeakingSet();
            set.Begin(1, 1, Emotion.Angry, SpeechKind.Assistant, new[] { 0.5f }, FrameMs, 0.0);

            // 採番のやり直しで PlaybackState.Items からは消えるが、ここは触られない
            Assert.That(set.Count, Is.EqualTo(1));

            Emotion emotion;
            SpeechKind kind;
            Assert.That(set.TryGetFace(out emotion, out kind), Is.True);
            Assert.That(emotion, Is.EqualTo(Emotion.Angry));
            Assert.That(set.Mouth(0.0, 0.01, 0), Is.EqualTo(0.5f).Within(1e-6f));
        }

        // ---- 後始末 ----

        [Test]
        public void EndAllClearsEverything()
        {
            var set = new SpeakingSet();
            set.Begin(1, 1, Emotion.Happy, SpeechKind.Assistant, new[] { 1f }, FrameMs, 0.0);
            set.Begin(1, 2, Emotion.Sad, SpeechKind.Assistant, new[] { 1f }, FrameMs, 0.0);

            set.EndAll();

            Assert.That(set.Count, Is.EqualTo(0));
            Emotion emotion;
            SpeechKind kind;
            Assert.That(set.TryGetFace(out emotion, out kind), Is.False);
        }

        /// <summary>
        /// ★ <c>End</c> は <c>PlayAsync</c> の <c>finally</c> から来るので、知らないキーでも
        ///   投げないこと。<c>epoch</c> が違えば別物として扱う（<c>seq</c> は世代を跨いで一意でない）。
        /// </summary>
        [Test]
        public void EndIgnoresUnknownKeys()
        {
            var set = WithOne(new[] { 1f });

            set.End(9, 9);
            set.End(2, 1);   // 同じ seq でも epoch が違えば別物
            Assert.That(set.Count, Is.EqualTo(1));

            set.End(1, 1);
            Assert.That(set.Count, Is.EqualTo(0));
        }

        [Test]
        public void BeginTwiceWithTheSameKeyDoesNotDuplicate()
        {
            var set = new SpeakingSet();
            set.Begin(1, 1, Emotion.Happy, SpeechKind.Assistant, new[] { 1f }, FrameMs, 0.0);
            set.Begin(1, 1, Emotion.Sad, SpeechKind.Assistant, new[] { 1f }, FrameMs, 0.0);

            Assert.That(set.Count, Is.EqualTo(1));

            Emotion emotion;
            SpeechKind kind;
            Assert.That(set.TryGetFace(out emotion, out kind), Is.True);
            Assert.That(emotion, Is.EqualTo(Emotion.Sad));
        }
    }
}
