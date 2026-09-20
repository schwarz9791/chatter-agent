using System;
using System.Collections.Generic;
using System.Globalization;
using ChatterMascot.Playback;
using ChatterMascot.Protocol;

namespace ChatterMascot.Tests
{
    /// <summary>
    /// <c>core/src/player/playbackQueue.test.ts</c> と対になるテストの土台。
    ///
    /// 状態機械が「イベントを入れるとコマンドの配列が返る」形なので、テストは
    /// <b>このイベント列でこのコマンド列が出る</b>を配列比較で固定できる。
    /// </summary>
    public abstract class PlaybackQueueTestBase
    {
        protected static readonly long T0 =
            DateTimeOffset.Parse("2026-08-15T00:00:00.000Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                .ToUnixTimeMilliseconds();

        /// <summary>
        /// サーバーが名乗る採番の世代。<see cref="E2"/> へ切り替えることが
        /// 「採番のやり直し」の再現になる。
        /// </summary>
        protected const string E1 = "gen-1";
        protected const string E2 = "gen-2";

        protected static string Iso(long unixMs)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
                .ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        /// <summary>サーバーが組み立てる音声パス。クライアントは組み立てないのでテスト側に置く。</summary>
        protected static string AudioPathOf(string epoch, long seq)
        {
            return "/audio/" + epoch + "-" + seq.ToString("D12", CultureInfo.InvariantCulture) + ".wav";
        }

        protected static SpeechFrame Record(
            long seq,
            string epoch = E1,
            string ts = null,
            bool noAudio = false,
            string audioPath = null,
            string text = null)
        {
            return new SpeechFrame
            {
                Epoch = epoch,
                Seq = seq,
                Ts = ts ?? Iso(T0 + seq * 1000),
                SessionId = "sess-1",
                TurnId = "turn-1",
                MessageId = "m1",
                Kind = SpeechKind.Assistant,
                Text = text ?? ("文" + seq + "。"),
                Emotion = Emotion.Neutral,
                Audio = noAudio ? null : new AudioRef(audioPath ?? AudioPathOf(epoch, seq)),
            };
        }

        /// <summary>既定は未接続。ほとんどのテストは繋がった状態を見たいので進めておく。</summary>
        protected static PlaybackState Start(Action<PlaybackOptions> configure = null)
        {
            var options = new PlaybackOptions();
            configure?.Invoke(options);
            var state = new PlaybackState(options);
            PlaybackQueue.Reduce(state, PlaybackEvent.Connected(), T0);
            return state;
        }

        /// <summary>イベントを順に流し、出たコマンドを全部集める。</summary>
        protected static List<PlaybackCommand> Run(PlaybackState state, IEnumerable<PlaybackEvent> events, long? now = null)
        {
            var at = now ?? T0;
            var all = new List<PlaybackCommand>();
            foreach (var ev in events) all.AddRange(PlaybackQueue.Reduce(state, ev, at));
            return all;
        }

        protected static List<PlaybackCommand> Run(PlaybackState state, PlaybackEvent ev, long? now = null)
        {
            return Run(state, new[] { ev }, now);
        }

        protected static List<PlaybackCommand> Only(IEnumerable<PlaybackCommand> commands, PlaybackCommandKind kind)
        {
            var result = new List<PlaybackCommand>();
            foreach (var c in commands)
            {
                if (c.Kind == kind) result.Add(c);
            }
            return result;
        }

        protected static List<long> SeqsOf(IEnumerable<PlaybackCommand> commands, PlaybackCommandKind kind)
        {
            var result = new List<long>();
            foreach (var c in Only(commands, kind)) result.Add(c.Seq);
            return result;
        }

        /// <summary>複数フレームの受信イベントを作る。</summary>
        protected static List<PlaybackEvent> ReceivedAll(params long[] seqs)
        {
            var events = new List<PlaybackEvent>();
            foreach (var seq in seqs) events.Add(PlaybackEvent.Received(Record(seq)));
            return events;
        }

        /// <summary>取得 → 再生 → 完了 を1文ぶん通す。</summary>
        protected static List<PlaybackCommand> Speak(PlaybackState state, long seq, long? now = null)
        {
            return Run(state, new[]
            {
                PlaybackEvent.AudioReady(0, seq, Clip(seq)),
                PlaybackEvent.Played(0, seq),
            }, now);
        }

        /// <summary>
        /// 音声ハンドルのダミー。状態機械は中身を知らないので、識別できれば何でもよい
        /// （実機では <c>AudioClip</c>）。
        /// </summary>
        protected static object Clip(long seq, int epoch = 0)
        {
            return "clip:" + epoch + ":" + seq;
        }
    }
}
