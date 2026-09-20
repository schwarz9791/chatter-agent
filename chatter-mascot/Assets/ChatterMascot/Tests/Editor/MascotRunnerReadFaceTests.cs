using ChatterMascot.Playback;
using ChatterMascot.Protocol;
using NUnit.Framework;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <see cref="MascotRunner.ReadFace"/>。<c>Play</c> コマンドの実行直前に、キューから
    /// その発話の表情を読む。
    ///
    /// ★★ <b>削除した <c>SpeakingViewTests</c> の穴を埋めるためのテスト。</b> あちらの
    ///   <c>DoesNotThrowWhenStateIsNull</c> / <c>DoesNotThrowWhenThePlayingItemHasNoRecord</c> が
    ///   守っていた「読めなくても既定値へ倒す」性質は、#58 で <c>MascotRunner</c> の private な
    ///   メソッドへ移り、<b>EditMode から1行も届かなくなっていた</b>（PR #74 のレビュー指摘）。
    ///
    /// ★ <b><c>MonoBehaviour</c> は作らない。</b> <c>MascotRunnerIsParkedTests</c> と同じで、
    ///   判定が <c>public static</c> に切り出してあるので直接呼べる。
    /// </summary>
    [TestFixture]
    public sealed class MascotRunnerReadFaceTests
    {
        private static PlaybackState StateWith(long seq, SpeechFrame record)
        {
            var state = new PlaybackState(null);
            state.Items[seq] = new QueueItem { Record = record, Status = ItemStatus.Playing };
            return state;
        }

        [Test]
        public void CopiesTheKindAndEmotionOfThePlayingItem()
        {
            var state = StateWith(42, new SpeechFrame
            {
                Seq = 42,
                Kind = SpeechKind.Prompt,
                Emotion = Emotion.Happy,
            });

            SpeechKind kind;
            Emotion emotion;
            MascotRunner.ReadFace(state, 42, out kind, out emotion);

            Assert.That(kind, Is.EqualTo(SpeechKind.Prompt));
            Assert.That(emotion, Is.EqualTo(Emotion.Happy));
        }

        /// <summary>
        /// ★ <see cref="MascotRunner.Start"/> の前は <c>_state</c> が <c>null</c>。
        ///   投げずに既定値へ倒すこと。
        /// </summary>
        [Test]
        public void FallsBackWhenTheStateIsNull()
        {
            SpeechKind kind;
            Emotion emotion;
            MascotRunner.ReadFace(null, 1, out kind, out emotion);

            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
        }

        [Test]
        public void FallsBackForAnUnknownSeq()
        {
            var state = StateWith(1, new SpeechFrame { Seq = 1, Emotion = Emotion.Angry });

            SpeechKind kind;
            Emotion emotion;
            MascotRunner.ReadFace(state, 99, out kind, out emotion);

            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
        }

        /// <summary>
        /// ★★ <b>ここで「登録を飛ばす」に書き換えないこと。</b> 飛ばすと
        ///   <c>SpeakingSet</c> に載らず、<b>鳴っているのに喋っていない</b>状態になって
        ///   口も表情も体の動きも止まる。<see cref="MascotRunner.ReadFace"/> が <c>void</c> なのは
        ///   呼び出し側にその分岐材料を渡さないため。
        /// </summary>
        [Test]
        public void FallsBackWhenTheItemHasNoRecord()
        {
            var state = new PlaybackState(null);
            state.Items[7] = new QueueItem { Record = null, Status = ItemStatus.Playing };

            SpeechKind kind;
            Emotion emotion;
            MascotRunner.ReadFace(state, 7, out kind, out emotion);

            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
        }

        [Test]
        public void FallsBackWhenTheItemIsNull()
        {
            var state = new PlaybackState(null);
            state.Items[7] = null;

            SpeechKind kind;
            Emotion emotion;
            MascotRunner.ReadFace(state, 7, out kind, out emotion);

            Assert.That(kind, Is.EqualTo(SpeechKind.Assistant));
            Assert.That(emotion, Is.EqualTo(Emotion.Neutral));
        }
    }
}
