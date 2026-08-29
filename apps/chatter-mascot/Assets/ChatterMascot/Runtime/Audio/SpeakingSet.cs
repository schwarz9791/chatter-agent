using System;
using System.Collections.Generic;
using ChatterMascot.Protocol;

namespace ChatterMascot.Audio
{
    /// <summary>
    /// <b>いま鳴っている発話の集合。</b>口の開きと表情をここから出す。<b>純粋。時計は引数で受け取る。</b>
    ///
    /// ★ <b><c>PlaybackQueue</c> には手を入れないこと。</b> <c>AudioIdleGate</c>（#55）と同じ流儀で、
    ///   ドライバ（<c>MascotRunner</c>）が <c>Play</c> コマンドを実行する直前に
    ///   <see cref="Begin"/>、<c>PlayAsync</c> の完了で <see cref="End"/> を呼ぶだけ。
    ///   状態機械にコマンドを増やすと、EditMode テストのコマンド列比較が全部壊れる。
    ///
    /// ★★ <b>これが <c>SpeakingView</c> を置き換える。</b> あちらは <c>PlaybackState.Items</c> の
    ///   <c>Status == Playing</c> を毎フレーム走査していたが、<c>PlaybackState.Orphans</c> は
    ///   音声ハンドルしか持たず <c>SpeechFrame</c>（<c>Record</c>）を持たないので、
    ///   <b>孤児（採番のやり直しで <c>Items</c> から外れたが鳴らし切っている音）の間は
    ///   常に <c>false</c> を返していた</b>。ここでは<b>再生を始めた時点で emotion / kind を
    ///   写し取る</b>ので、孤児になった後も保持できる。
    ///
    /// ★ <b>孤児が鳴っている間の口は全発話の <c>max</c>。</b> 口は1つでスピーカーも1つなので、
    ///   「今いちばん大きく鳴っている音」に合わせるのが物理的に正しい。孤児が鳴り切るまで
    ///   口が動き続けるのも契約（「音は最後まで流す」）どおり。
    ///
    /// ★ <b><see cref="TryGetFace"/> は最後に始まったものを返す。</b> 表情は「今の話題」に
    ///   従うべきで、消えゆく旧エポックではない。
    /// </summary>
    public sealed class SpeakingSet
    {
        private sealed class Entry
        {
            public int Epoch;
            public long Seq;
            public Emotion Emotion;
            public SpeechKind Kind;
            public float[] Envelope;
            public int FrameMs;
            public double StartedAt;

            /// <summary>始まった順。<see cref="TryGetFace"/> が「最後」を決めるのに使う</summary>
            public long Order;
        }

        // 定常状態では1件、孤児が重なっているときだけ2件以上。Dictionary を持つ意味が無い
        private readonly List<Entry> _entries = new List<Entry>();
        private long _nextOrder;

        /// <summary>いま鳴っている件数（<b>孤児を含む</b>）。0 なら喋っていない</summary>
        public int Count
        {
            get { return _entries.Count; }
        }

        /// <summary>
        /// 再生を始めた。
        ///
        /// ★ <b><c>MascotRunner</c> が <c>PlayAsync</c> を呼ぶ<u>直前</u>に呼ぶこと。</b>
        ///   <c>PlayAsync</c> の中に入れると、同期完了する経路（「音声のハンドルがありません」など）で
        ///   <c>Dispatch</c> がコマンドループへ再入するため、入れ子の順序が読めなくなる。
        /// ★ <paramref name="envelope"/> が <c>null</c> でも受け付ける（口が動かないだけで、
        ///   <see cref="Count"/> と <see cref="TryGetFace"/> には数える）。
        ///   <b>エンベロープが作れないことを「喋っていない」と読み替えないこと</b> ——
        ///   表情と体の動きまで止まる。
        /// </summary>
        /// <param name="startedAt"><c>Time.realtimeSinceStartupAsDouble</c>（秒）</param>
        public void Begin(
            int epoch, long seq, Emotion emotion, SpeechKind kind,
            float[] envelope, int frameMs, double startedAt)
        {
            // 同じキーで二度始まることは無いはずだが、あっても壊れないように上書きする
            Remove(epoch, seq);

            _entries.Add(new Entry
            {
                Epoch = epoch,
                Seq = seq,
                Emotion = emotion,
                Kind = kind,
                Envelope = envelope,
                FrameMs = frameMs,
                StartedAt = startedAt,
                Order = _nextOrder++,
            });
        }

        /// <summary>
        /// 鳴り終わった（<b>成功も失敗も</b>）。知らないキーなら何もしない。
        ///
        /// ★ <b><c>PlayAsync</c> の <c>finally</c> から呼ぶこと。</b> 例外や終了処理で
        ///   落とすと、<b>そのエントリが残り続けて口が開きっぱなしで固まる</b>。
        /// </summary>
        public void End(int epoch, long seq)
        {
            Remove(epoch, seq);
        }

        /// <summary>全部やめる（終了処理・<c>StopAll</c> 用）。</summary>
        public void EndAll()
        {
            _entries.Clear();
        }

        /// <summary>
        /// 最後に鳴り始めた発話の表情。<b>無ければ <c>false</c>。</b>
        ///
        /// ★ <b><c>false</c> のときは <c>Assistant</c> / <c>Neutral</c> に倒す契約</b>
        ///   （<c>SpeakingView.TryRead</c> から移送した。<c>VrmCharacter.LateUpdate</c> が
        ///   この契約に寄りかかっていて、呼び出し側で <c>Speaking ? kind : 既定</c> と
        ///   書き直していない）。
        /// </summary>
        public bool TryGetFace(out Emotion emotion, out SpeechKind kind)
        {
            emotion = Emotion.Neutral;
            kind = SpeechKind.Assistant;

            Entry latest = null;
            foreach (var entry in _entries)
            {
                if (latest == null || entry.Order > latest.Order) latest = entry;
            }
            if (latest == null) return false;

            emotion = latest.Emotion;
            kind = latest.Kind;
            return true;
        }

        /// <summary>
        /// 区間 <c>[from, to]</c>（秒）における<b>全発話の RMS の最大値</b>。生の値（ゲイン前）。
        ///
        /// ★★ <b>点ではなく区間で読むこと。</b> エンベロープの刻み（20ms）と表示（30fps = 33.3ms）は
        ///   割り切れないので、点サンプリングすると<b>4割のフレームを読み飛ばす</b>＝
        ///   立ち上がりが落ちて口が鈍る。前フレームからの区間の最大を取れば 30fps でも保たれる。
        ///
        /// ★★ <b><paramref name="offsetMs"/> の索引で「負を 0 にクランプ」しないこと。</b>
        ///   <c>index = max(0, index)</c> と書くと、offset ぶんの先行区間で <c>envelope[0]</c> を
        ///   返す＝<b>音より先に口が動く</b>ので、offset を入れた意味が消える。
        ///   <b>区間全体が音より前（<c>hi &lt; 0</c>）なら 0（口を閉じたまま）</b>が正しい。
        /// </summary>
        /// <param name="offsetMs">
        /// 再生の開始が実際に音になるまでのラグ。<c>afplay</c> は <c>Process.Start</c> が
        /// 音より前に返るぶんだけ正の値になる（Unity 内蔵オーディオの実装では 0）。
        /// </param>
        public float Mouth(double from, double to, int offsetMs)
        {
            if (double.IsNaN(from) || double.IsNaN(to)) return 0f;

            // 時計の逆行（本来 realtimeSinceStartup では起きない）でも壊れないように
            if (from > to)
            {
                var swap = from;
                from = to;
                to = swap;
            }

            var max = 0f;
            foreach (var entry in _entries)
            {
                var envelope = entry.Envelope;
                if (envelope == null || envelope.Length == 0 || entry.FrameMs <= 0) continue;

                var loFrame = Math.Floor(((from - entry.StartedAt) * 1000.0 - offsetMs) / entry.FrameMs);
                var hiFrame = Math.Floor(((to - entry.StartedAt) * 1000.0 - offsetMs) / entry.FrameMs);

                // まだ音が出ていない / もう鳴り終わっている
                if (hiFrame < 0.0) continue;
                if (loFrame >= envelope.Length) continue;

                // ★ 桁が離れた double をそのまま (int) にすると未定義値になる。
                //   上の2つの早期 continue を通った後で範囲へ落とす
                var lo = loFrame < 0.0 ? 0 : (int)loFrame;
                var hi = hiFrame >= envelope.Length ? envelope.Length - 1 : (int)hiFrame;

                for (var i = lo; i <= hi; i++)
                {
                    if (envelope[i] > max) max = envelope[i];
                }
            }

            return max;
        }

        private void Remove(int epoch, long seq)
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Epoch != epoch || _entries[i].Seq != seq) continue;
                _entries.RemoveAt(i);
                return;
            }
        }
    }
}
