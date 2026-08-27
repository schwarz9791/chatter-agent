using ChatterMascot.Protocol;

namespace ChatterMascot.Playback
{
    /// <summary>
    /// <see cref="PlaybackState"/> から「いま何を喋っているか」を読む。<b>純粋・読むだけ。</b>
    ///
    /// #59（アイドル・視線）と #57（表情）がこれを使って <c>kind</c> / <c>emotion</c> を得る。
    /// <b>暫定品。</b> #58 の <c>SpeakingSet</c> が入ったら用済みになる。
    ///
    /// ★ <b><c>state.Orphans</c> は見ない。</b> <c>Orphans</c> の値は音声ハンドル
    ///   （<c>Dictionary&lt;string, object&gt;</c>）だけを持ち、<c>SpeechFrame</c>（<c>Record</c>）を
    ///   持たないので、<c>kind</c> / <c>emotion</c> を原理的に読めない。孤児（採番のやり直しで
    ///   <c>Items</c> から外れたが鳴らし切っている音）が再生中の間はこのメソッドが <c>false</c> を
    ///   返す＝アイドルが発話中モードへ上がらない、という<b>既知の穴</b>。ここで直そうとしないこと。
    ///   #58 の <c>SpeakingSet</c> が解決する。
    /// </summary>
    public static class SpeakingView
    {
        /// <summary>
        /// <c>state.Items</c> から <c>Status == Playing</c> の item を探す。
        ///
        /// ★ 再生されるのは head だけなので <c>Playing</c> は高々1件のはずだが、
        ///   複数あれば<b>防御的に seq 最小</b>を採る。
        /// ★ <c>state == null</c> / <c>item.Record == null</c> でも投げない。
        /// </summary>
        public static bool TryRead(PlaybackState state, out SpeechKind kind, out Emotion emotion)
        {
            kind = SpeechKind.Assistant;
            emotion = Emotion.Neutral;

            if (state == null) return false;

            QueueItem playing = null;
            long playingSeq = 0;

            foreach (var pair in state.Items)
            {
                var item = pair.Value;
                if (item == null || item.Status != ItemStatus.Playing) continue;

                if (playing == null || pair.Key < playingSeq)
                {
                    playing = item;
                    playingSeq = pair.Key;
                }
            }

            if (playing == null || playing.Record == null) return false;

            kind = playing.Record.Kind;
            emotion = playing.Record.Emotion;
            return true;
        }
    }
}
