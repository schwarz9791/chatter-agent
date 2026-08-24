using System.Collections.Generic;
using ChatterMascot.Protocol;

namespace ChatterMascot.Playback
{
    public enum ItemStatus
    {
        Pending,
        Fetching,
        Ready,
        Playing,
        Done,
    }

    public sealed class QueueItem
    {
        public SpeechFrame Record;
        public ItemStatus Status;

        /// <summary><see cref="ItemStatus.Ready"/> 以降で、取得済みの音声ハンドル。</summary>
        public object Audio;

        /// <summary>取得を試みた回数。<c>SynthesisAttempts</c> に達したら諦める。</summary>
        public int Attempts;

        /// <summary>
        /// この時刻までは取りに行かない。503（あとで取りに来い）を受けたときに置く。
        ///
        /// ★ 503 で <see cref="Attempts"/> を消費しないこと。消費すると、サーバー側でエンジンが
        ///   落ちているだけで<b>溜まっていたキューが数百 ms で全部捨てられる</b>。
        /// </summary>
        public long RetryAfter;
    }

    /// <summary>切断中に確定した ack。累積 ack なので最大値だけ意味がある。</summary>
    public struct PendingAckEntry
    {
        /// <summary><b>どのエポックのものか。</b> 旧エポックの ack を新しいサーバーへ打つと、
        /// まだ喋っていない entry が消える。</summary>
        public int Epoch;
        public long Seq;
    }

    public sealed class PlaybackState
    {
        public readonly PlaybackOptions Options;

        /// <summary>
        /// 現在のエポックの通し番号。採番がやり直されるたびに +1 する。
        ///
        /// ★ <b>プロセス内のカウンタで、サーバー由来の <see cref="EpochId"/> とは別物。</b>
        ///   サーバーの epoch は外部由来の文字列なので、そのままキャッシュのキーやファイル名に
        ///   入れない。<c>Seq</c> がエポックを跨いで一意でない以上、<b>非同期の結果・孤児・
        ///   保留 ack を (Epoch, Seq) で識別する</b>必要は残るので、内側では連番に読み替えて使う。
        /// </summary>
        public int Epoch;

        /// <summary>
        /// サーバーが名乗っている採番の世代（<c>SpeechFrame.Epoch</c>）。未受信なら <c>null</c>。
        /// これが変わった＝<b>採番がやり直された</b>。契約で運ばれてくるので推論しない。
        /// </summary>
        public string EpochId;

        /// <summary>seq → item。順序は seq の昇順で都度求める（挿入順とは限らない）。</summary>
        public readonly Dictionary<long, QueueItem> Items = new Dictionary<long, QueueItem>();

        /// <summary>
        /// 消費済みの <c>"{EpochId}:{Seq}"</c>。
        ///
        /// ★ <c>Seq</c> 単独（<c>HashSet&lt;long&gt;</c>）にしないこと。ランタイムルートを消すと
        ///   CLI の採番が 1 からやり直される。seq だけで覚えていると、新しい seq 1..N が
        ///   「もう喋った」と判定されて<b>何百文でも一切喋らず、エラーも出ない</b>。
        /// </summary>
        public readonly HashSet<string> Seen = new HashSet<string>();

        /// <summary>
        /// <see cref="Seen"/> の挿入順。追い出しに使う。
        ///
        /// ★ <b>数値の最小から追い出さないこと。</b> 採番やり直しの直後に「新しく来た小さい seq」を
        ///   優先的に忘れることになり、次の再送で二度読み上げる。JS の <c>Set</c> は挿入順を保つが、
        ///   C# の <c>HashSet</c> には順序が無いので、この Queue で補う。
        /// </summary>
        public readonly Queue<string> SeenOrder = new Queue<string>();

        /// <summary>
        /// <b>消費した</b>（＝ack を打った）最大の seq。<see cref="Seen"/> から溢れた再送の検出に使う。
        ///
        /// ★ <b>エポック変化とは別物として残すこと。</b> 世代が変わったかどうかは
        ///   <see cref="EpochId"/> で決まるが、「<see cref="Seen"/> の上限から溢れた消費済み entry の
        ///   再送」は<b>同じ世代の中で</b>起きる。<c>SeenCapacity</c> はサーバー側のキュー上限と
        ///   ズレうるので溢れは起きるし、それを世代の変化と読むと状態を捨てて<b>同じ文を2回喋る</b>。
        /// </summary>
        public long MaxSeqConsumed;

        public PendingAckEntry? PendingAck;

        public bool Connected;

        /// <summary>
        /// エポックリセットで <see cref="Items"/> から外した、再生中の item。キーは <c>"{Epoch}:{Seq}"</c>。
        /// 音は最後まで流すが、完了しても ack しない（もう別のエポックなので意味を持たない）。
        /// </summary>
        public readonly Dictionary<string, object> Orphans = new Dictionary<string, object>();

        /// <summary>head の走査結果。<c>null</c> = 未計算、<c>-1</c> = 空。</summary>
        public long? HeadCache;

        /// <summary>stall watchdog: 現在の head と、それが head になった時刻。</summary>
        public long? HeadSeq;

        public long HeadSince;

        /// <summary>最後に停滞を警告した時刻。0 なら未出力。</summary>
        public long StallWarnedAt;

        /// <summary>音声を用意できなかった回数の連続。<c>AudioReady</c> で 0 に戻る。</summary>
        public int UnavailableStreak;

        /// <summary>最後に「用意できない」警告を出した時刻。0 なら未出力。</summary>
        public long UnavailableWarnedAt;

        /// <summary>503 の連続回数。取り直し間隔のバックオフに使う。<c>AudioReady</c> で 0 に戻る。</summary>
        public int UnavailableBackoffStep;

        public PlaybackState(PlaybackOptions options)
        {
            Options = options ?? new PlaybackOptions();
        }
    }
}
