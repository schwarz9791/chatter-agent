using System;
using System.Collections.Generic;
using System.Globalization;
using ChatterMascot.Protocol;

namespace ChatterMascot.Playback
{
    /// <summary>
    /// 発話キューの判断だけを持つ部品。<b>取得も再生も ack もここでは行わない。</b>
    /// <c>core/src/player/playbackQueue.ts</c> の移植。
    ///
    /// <b>イベントを入れるとコマンドの配列が返る</b>形にしてある。副作用は
    /// <c>MascotRunner</c>（ドライバ）が実行し、その結果をまたイベントとして戻す。
    /// 完了コールバックが状態機械に再入する形（「ループの途中で状態が変わる」再入バグ）を
    /// テストで捕まえられるようにするための構造で、テストは
    /// 「このイベント列でこのコマンド列が出る」を配列比較で固定できる。
    ///
    /// <c>state</c> は同じオブジェクトを in-place で更新する。「純粋」の意味はあくまで
    /// <b>外部への副作用が無い</b>ことで、不変性ではない。
    /// </summary>
    public static class PlaybackQueue
    {
        /// <summary>
        /// イベントを1つ入れて、実行すべきコマンドを受け取る。<c>state</c> は in-place で更新される。
        /// </summary>
        public static List<PlaybackCommand> Reduce(PlaybackState state, PlaybackEvent ev, long now)
        {
            var commands = new List<PlaybackCommand>();

            switch (ev.Kind)
            {
                case PlaybackEventKind.Received:
                    OnReceived(state, ev.Record, commands);
                    break;

                case PlaybackEventKind.AudioReady:
                {
                    // ★ エポックを先に見る。採番のやり直しを跨いだ結果を新しい item に入れると、
                    //   別の文の音声で ready になり、鳴っている内容と ack がずれる
                    var item = FindItem(state, ev.Epoch, ev.Seq);
                    // エポックリセットや stale で消えた後に取得が返ってきた。音声だけ捨てる
                    if (item == null || item.Status != ItemStatus.Fetching)
                    {
                        commands.Add(PlaybackCommand.DiscardAudio(ev.Epoch, ev.Seq, ev.Audio));
                        break;
                    }
                    item.Status = ItemStatus.Ready;
                    item.Audio = ev.Audio;
                    // 取れたので、バックオフも警告のラッチも解く
                    state.UnavailableStreak = 0;
                    state.UnavailableBackoffStep = 0;
                    state.UnavailableWarnedAt = 0;
                    break;
                }

                case PlaybackEventKind.AudioUnavailable:
                {
                    // 503。サーバーはいるが用意できていない。**試行回数を消費しない**
                    var item = FindItem(state, ev.Epoch, ev.Seq);
                    if (item == null || item.Status != ItemStatus.Fetching) break;
                    item.Status = ItemStatus.Pending;
                    // ★ 諦めない以上、止めるのはバックオフの役目。固定間隔だと
                    //   エンジンが落ちている間ずっと窓ぶんのリクエストが飛び続ける
                    //
                    // ★ シフト量を頭打ちにすること。エンジンが長時間落ちていると
                    //   UnavailableBackoffStep は増え続けるので、2^step が long を溢れて
                    //   **負の待ち時間**になり、バックオフが完全に無効化される
                    //   （＝1フレームごとに取り直しが飛ぶ）。上限で押さえたあと
                    //   AudioRetryMaxMs で切るので、値そのものは変わらない
                    var shift = Math.Min(state.UnavailableBackoffStep, 30);
                    var backoff = state.Options.AudioRetryMs * (1L << shift);
                    item.RetryAfter = now + Math.Min(backoff, state.Options.AudioRetryMaxMs);
                    state.UnavailableBackoffStep++;
                    NoteUnavailable(state, now, ev.Reason, commands);
                    break;
                }

                case PlaybackEventKind.AudioGone:
                {
                    // 404。永久に用意できない。「長さ0の再生」として終端へ落とす
                    var item = FindItem(state, ev.Epoch, ev.Seq);
                    if (item == null || item.Status != ItemStatus.Fetching) break;
                    commands.Add(PlaybackCommand.Warn($"seq={ev.Seq} の音声がありません: {ev.Reason}"));
                    NoteUnavailable(state, now, ev.Reason, commands);
                    Finish(item);
                    break;
                }

                case PlaybackEventKind.AudioFailed:
                {
                    var item = FindItem(state, ev.Epoch, ev.Seq);
                    if (item == null || item.Status != ItemStatus.Fetching) break;
                    // ★ 数えるのはここ。FillWindow ではない（→ FillWindow のコメント）
                    item.Attempts++;
                    if (item.Attempts < state.Options.SynthesisAttempts)
                    {
                        // pending へ戻せば、次の Step で窓が拾い直す
                        item.Status = ItemStatus.Pending;
                        break;
                    }
                    commands.Add(PlaybackCommand.Warn($"seq={ev.Seq} の音声を取れなかったので飛ばします: {ev.Reason}"));
                    Finish(item);
                    break;
                }

                case PlaybackEventKind.Played:
                {
                    if (SettleOrphan(state, ev.Epoch, ev.Seq, commands)) break;
                    var item = FindItem(state, ev.Epoch, ev.Seq);
                    if (item == null || item.Status != ItemStatus.Playing) break;
                    Finish(item);
                    break;
                }

                case PlaybackEventKind.PlaybackFailed:
                {
                    if (SettleOrphan(state, ev.Epoch, ev.Seq, commands)) break;
                    var item = FindItem(state, ev.Epoch, ev.Seq);
                    if (item == null || item.Status != ItemStatus.Playing) break;
                    // 再生はリトライしない。途中まで鳴った文がもう一度頭から鳴る
                    commands.Add(PlaybackCommand.Warn($"seq={ev.Seq} の再生に失敗しました: {ev.Reason}"));
                    Finish(item);
                    break;
                }

                case PlaybackEventKind.Connected:
                    // ★ ここで保留 ack を流さないこと。WebSocket は必ず接続 → 受信の順なので、
                    //   この時点では**サーバーが作り直されたかどうかを知る手段が無い**
                    //   （epoch が載るのはフレームで、ハンドシェイクではない）。
                    //   旧エポックの ack を新しいサーバーに打つと、サーバーの ackUpTo は
                    //   ファイル名で範囲削除するため、**配信済み・未発話の entry**がまとめて消える。
                    //   最初のフレームでエポックが変わっていないと確認できてから FlushPendingAck で流す
                    state.Connected = true;
                    break;

                case PlaybackEventKind.Disconnected:
                    // ★ Items を触らない。再送は同じ内容で来るので重複排除が拾うし、捨てると
                    //   切断のたびに取得をやり直して無音が入る。再生中の音も止めない
                    state.Connected = false;
                    break;

                case PlaybackEventKind.Tick:
                    break;
            }

            Step(state, now, commands);
            return commands;
        }

        /// <summary>
        /// エポックが一致する item を返す。一致しなければ <c>null</c>。
        ///
        /// ★ <b>非同期の結果は必ずこれを通すこと。</b> <c>Seq</c> はエポックを跨いで一意でない。
        /// </summary>
        private static QueueItem FindItem(PlaybackState state, int epoch, long seq)
        {
            if (epoch != state.Epoch) return null;
            QueueItem item;
            return state.Items.TryGetValue(seq, out item) ? item : null;
        }

        private static string SeenKey(SpeechFrame record)
        {
            return record.Epoch + ":" + record.Seq.ToString(CultureInfo.InvariantCulture);
        }

        private static string OrphanKey(int epoch, long seq)
        {
            return epoch.ToString(CultureInfo.InvariantCulture) + ":" + seq.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 最小の seq を <c>k</c> 件だけ、昇順で返す。
        ///
        /// ★ 全体をソートしないこと。必要なのは窓のぶんだけで、<c>Items</c> は
        ///   <c>speechQueueMaxEntries</c>（既定 500）まで育つ。接続直後の追いつきでは
        ///   フレームごとに <see cref="Step"/> が回るので、毎回 O(n log n) を払うと詰まる。
        /// </summary>
        private static List<long> SmallestSeqs(PlaybackState state, int k)
        {
            var result = new List<long>();
            if (k <= 0) return result;

            foreach (var seq in state.Items.Keys)
            {
                if (result.Count < k)
                {
                    result.Add(seq);
                    result.Sort();
                    continue;
                }
                if (seq >= result[result.Count - 1]) continue;
                result[result.Count - 1] = seq;
                result.Sort();
            }
            return result;
        }

        /// <summary>
        /// head（最小 seq の item）。
        ///
        /// ★ 呼ばれる回数が多い（<see cref="Step"/> の1周で3回 + <see cref="CheckStall"/>）ので、
        ///   走査結果を持ち回る。挿入では最小値を更新するだけ、削除では head が消えたときにだけ再走査する。
        /// </summary>
        private static QueueItem HeadItem(PlaybackState state)
        {
            if (state.HeadCache == null)
            {
                long min = long.MaxValue;
                foreach (var seq in state.Items.Keys)
                {
                    if (seq < min) min = seq;
                }
                state.HeadCache = min == long.MaxValue ? -1 : min;
            }

            if (state.HeadCache.Value < 0) return null;
            QueueItem item;
            return state.Items.TryGetValue(state.HeadCache.Value, out item) ? item : null;
        }

        private static void TrackInsert(PlaybackState state, long seq)
        {
            if (state.HeadCache == null) return;
            if (state.HeadCache.Value < 0 || seq < state.HeadCache.Value) state.HeadCache = seq;
        }

        private static void TrackDelete(PlaybackState state, long seq)
        {
            if (state.HeadCache != null && state.HeadCache.Value == seq) state.HeadCache = null;
        }

        /// <summary>消費した（＝ack を打った）ことを覚える。エポック判定の基準はここだけで進む。</summary>
        private static void Remember(PlaybackState state, SpeechFrame record)
        {
            var key = SeenKey(record);
            // ★ 追い出しは**挿入順**。数値の最小から追い出すと、採番やり直しの直後に
            //   「新しく来た小さい seq」を優先的に忘れることになり、次の再送で二度読み上げる
            if (state.Seen.Add(key)) state.SeenOrder.Enqueue(key);
            while (state.Seen.Count > state.Options.SeenCapacity && state.SeenOrder.Count > 0)
            {
                state.Seen.Remove(state.SeenOrder.Dequeue());
            }

            if (record.Seq > state.MaxSeqConsumed) state.MaxSeqConsumed = record.Seq;
        }

        private static bool IsStale(PlaybackState state, QueueItem item, long now)
        {
            var maxAgeMs = state.Options.MaxAgeMs;
            if (maxAgeMs <= 0) return false;

            DateTimeOffset parsed;
            var ok = DateTimeOffset.TryParse(
                item.Record.Ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed);
            // 読めない ts で発話を捨てない
            if (!ok) return false;

            return now - parsed.ToUnixTimeMilliseconds() > maxAgeMs;
        }

        /// <summary>
        /// 終端へ落とす。
        ///
        /// ★ 失敗も再生完了もすべてここを通すこと。失敗を見つけた瞬間に ack を打つと、
        ///   先読みのぶんだけ head を追い越す。ack は累積で、サーバーの <c>ackUpTo</c> は
        ///   <c>seq &lt;= upTo</c> を<b>ファイル名で範囲削除</b>するので、まだ喋っていない手前の entry が
        ///   キューから消え、そこから先の任意の切断で失われる。
        ///   <b>失敗は「長さ 0 の再生」として扱う</b>、と考えると迷わない。
        /// </summary>
        private static void Finish(QueueItem item)
        {
            item.Status = ItemStatus.Done;
        }

        /// <summary>
        /// 音声を用意できなかったことを数える。続くようなら<b>設定ミスを疑う手がかり</b>を出す。
        ///
        /// 合成がサーバーへ移った結果、<c>ttsBaseUrl</c> / <c>ttsSpeakerId</c> の間違いも
        /// エンジンの不在も、クライアント側では区別できない。停滞警告より早く「どこを見ればいいか」を残す。
        /// </summary>
        private static void NoteUnavailable(PlaybackState state, long now, string reason, List<PlaybackCommand> commands)
        {
            state.UnavailableStreak++;

            var warnAfter = state.Options.UnavailableWarnAfter;
            if (warnAfter <= 0) return;
            if (state.UnavailableStreak < warnAfter) return;

            // ★ 時間で再武装する。件数のラッチだと、復旧して再び壊れたときに無言になる
            if (state.UnavailableWarnedAt != 0)
            {
                var repeat = state.Options.UnavailableWarnRepeatMs;
                if (repeat <= 0) return;
                if (now - state.UnavailableWarnedAt < repeat) return;
            }

            state.UnavailableWarnedAt = now;
            commands.Add(PlaybackCommand.Warn(
                $"音声を用意できない状態が {state.UnavailableStreak} 件続いています（{reason}）。" +
                "サーバー側の合成エンジンと ttsBaseUrl / ttsSpeakerId を確認してください"));
        }

        /// <summary>ack を出すか、切断中なら溜める。</summary>
        private static void EmitAck(PlaybackState state, long seq, List<PlaybackCommand> commands)
        {
            if (state.Connected && state.EpochId != null)
            {
                commands.Add(PlaybackCommand.Ack(seq, state.EpochId));
                return;
            }

            // 溜めるときはエポックごと覚える。エポックが変われば ResetEpoch が捨てる
            var held = state.PendingAck;
            if (held != null && held.Value.Epoch == state.Epoch)
            {
                state.PendingAck = new PendingAckEntry { Epoch = state.Epoch, Seq = Math.Max(held.Value.Seq, seq) };
            }
            else
            {
                state.PendingAck = new PendingAckEntry { Epoch = state.Epoch, Seq = seq };
            }
        }

        /// <summary>
        /// 切断中に溜めた ack を、<b>エポックが変わっていないと確認できてから</b>送る。
        ///
        /// ★ <c>Connected</c> では呼ばない。呼び出し口は「今のエポックのフレームを受け入れた直後」
        ///   の1箇所だけにする。
        /// </summary>
        private static void FlushPendingAck(PlaybackState state, List<PlaybackCommand> commands)
        {
            var held = state.PendingAck;
            if (held == null || !state.Connected) return;
            state.PendingAck = null;
            if (held.Value.Epoch == state.Epoch && state.EpochId != null)
            {
                commands.Add(PlaybackCommand.Ack(held.Value.Seq, state.EpochId));
            }
        }

        /// <summary>孤児（エポックリセットで Items から外した再生中の item）の後始末。扱ったら true。</summary>
        private static bool SettleOrphan(PlaybackState state, int epoch, long seq, List<PlaybackCommand> commands)
        {
            var key = OrphanKey(epoch, seq);
            object audio;
            if (!state.Orphans.TryGetValue(key, out audio)) return false;
            if (audio != null) commands.Add(PlaybackCommand.DiscardAudio(epoch, seq, audio));
            state.Orphans.Remove(key);
            return true;
        }

        /// <summary>古くなった pending / ready を落とす。再生中には触らない（もう鳴っている）。</summary>
        private static bool MarkStale(PlaybackState state, long now, List<PlaybackCommand> commands)
        {
            // 既定（0 = 無効）では判定するものが無い。Step のループから毎回呼ばれるので入口で抜ける
            if (state.Options.MaxAgeMs <= 0) return false;

            var changed = false;
            foreach (var item in state.Items.Values)
            {
                if (item.Status != ItemStatus.Pending && item.Status != ItemStatus.Ready) continue;
                if (!IsStale(state, item, now)) continue;
                commands.Add(PlaybackCommand.Log($"seq={item.Record.Seq} は古いので飛ばします"));
                Finish(item);
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// head が <c>Done</c> である限り消費し、まとめて1回だけ ack する。
        /// 累積 ack なので、連続した done に対して ack を何度も打つ必要は無い。
        /// </summary>
        private static bool ConsumeHead(PlaybackState state, List<PlaybackCommand> commands)
        {
            long? acked = null;

            for (;;)
            {
                var head = HeadItem(state);
                if (head == null || head.Status != ItemStatus.Done) break;

                var seq = head.Record.Seq;
                if (head.Audio != null) commands.Add(PlaybackCommand.DiscardAudio(state.Epoch, seq, head.Audio));
                state.Items.Remove(seq);
                TrackDelete(state, seq);
                Remember(state, head.Record);
                acked = seq;
            }

            if (acked == null) return false;
            EmitAck(state, acked.Value, commands);
            return true;
        }

        /// <summary>head が鳴らせる状態なら鳴らす。head より先には進まない（順序の保証はここ1点）。</summary>
        private static bool StartPlayback(PlaybackState state, List<PlaybackCommand> commands)
        {
            var head = HeadItem(state);
            if (head == null || head.Status != ItemStatus.Ready || head.Audio == null) return false;
            head.Status = ItemStatus.Playing;
            commands.Add(PlaybackCommand.Play(state.Epoch, head.Record.Seq, head.Audio));
            return true;
        }

        /// <summary>
        /// 先読みの窓を埋める。
        ///
        /// ★ 窓は「seq 昇順に並べた<b>生存 item の位置</b>で先頭 <c>Lookahead + 1</c> 件」。
        ///   <b>「再生中の seq + Lookahead」という数値の窓にしないこと。</b> seq の飛びは仕様
        ///   （CLI の trim、サーバー再起動）で、一度飛ぶと数値窓は対象ゼロになり、
        ///   <b>音は出るのに先読みだけが恒久的に無効化される</b>（しかも気づけない）。
        /// </summary>
        private static bool FillWindow(PlaybackState state, long now, List<PlaybackCommand> commands)
        {
            var window = SmallestSeqs(state, state.Options.Lookahead + 1);
            var changed = false;

            foreach (var seq in window)
            {
                QueueItem item;
                if (!state.Items.TryGetValue(seq, out item)) continue;
                // done は窓の枠を1つ食うが、head に来た瞬間に消えるので実害は無い。
                // ここで選んでしまうと取得を無限に投げ直すことになるので必ず除く
                if (item.Status != ItemStatus.Pending) continue;

                // ★ 音声が無いフレームは取りに行かない。約物だけの断片や ttsEnabled: false が
                //   これで、サーバーは audio: null を載せてくる。「長さ0の再生」として終端へ落とす
                if (item.Record.Audio == null)
                {
                    Finish(item);
                    changed = true;
                    continue;
                }

                // 503 のバックオフ中。Tick で now が進めば次のパスで拾われる
                if (now < item.RetryAfter) continue;

                // ★ ここで Attempts++ しないこと。503（＝試行回数を消費しない結果）のたびに
                //   打ち消す -- が要る形になり、上限がいつ来るのか読み解けなくなる。
                //   **消費する場所（AudioFailed）で数える**
                item.Status = ItemStatus.Fetching;
                commands.Add(PlaybackCommand.FetchAudio(state.Epoch, seq, item.Record.Audio.Path));
                changed = true;
            }

            return changed;
        }

        private static void CheckStall(PlaybackState state, long now, List<PlaybackCommand> commands)
        {
            var head = HeadItem(state);
            long? seq = head == null ? (long?)null : head.Record.Seq;

            if (seq != state.HeadSeq)
            {
                state.HeadSeq = seq;
                state.HeadSince = now;
                state.StallWarnedAt = 0;
                return;
            }

            var stallWarnMs = state.Options.StallWarnMs;
            if (stallWarnMs <= 0 || seq == null) return;
            if (now - state.HeadSince < stallWarnMs) return;
            // ★ **HeadSeq が変わるまで再武装しない形にしないこと。** 恒久的に詰まると
            //   head が永遠に変わらないので、生涯1行しか出なくなる。StallWarnMs ごとに出し直す
            if (state.StallWarnedAt != 0 && now - state.StallWarnedAt < stallWarnMs) return;

            // 無音は症状として何も語らない。最後の保険として、どこで止まったかだけは残す
            state.StallWarnedAt = now;
            var status = head == null ? "?" : head.Status.ToString();
            var seconds = (long)Math.Round((now - state.HeadSince) / 1000.0);
            commands.Add(PlaybackCommand.Warn($"seq={seq} が {seconds} 秒 {status} のまま進んでいません"));
        }

        /// <summary>
        /// 状態を進められるだけ進める。
        ///
        /// ★ <b>すべてのイベントから必ずこれを通すこと。</b> 「消費したときに窓を再評価する」経路が
        ///   抜けていると、<c>Lookahead + 1</c> 文目以降が永久に無音になる（移植で最も踏みやすい穴）。
        /// </summary>
        private static void Step(PlaybackState state, long now, List<PlaybackCommand> commands)
        {
            // 各操作は item を減らすか status を単調に進めるので必ず収束する。
            // guard は将来の改変に対する保険
            for (var guard = 0; guard < 1000; guard++)
            {
                var staled = MarkStale(state, now, commands);
                var consumed = ConsumeHead(state, commands);
                var started = StartPlayback(state, commands);
                var filled = FillWindow(state, now, commands);
                if (!staled && !consumed && !started && !filled) break;
            }
            CheckStall(state, now, commands);
        }

        /// <summary>
        /// 採番がやり直された（ランタイムルートの削除、バックアップ復元）ときの後始末。
        ///
        /// 再生中のものだけは最後まで流す（音を途中で切る方が事故に聞こえる）が、
        /// <b>その完了で ack は打たない</b>。もう別のエポックなので、その seq に意味が無い。
        /// </summary>
        private static void ResetEpoch(PlaybackState state, List<PlaybackCommand> commands)
        {
            commands.Add(PlaybackCommand.Warn("seq の採番がやり直されました。再生キューの状態をリセットします"));

            var previous = state.Epoch;
            state.Epoch++;

            foreach (var pair in new List<KeyValuePair<long, QueueItem>>(state.Items))
            {
                var seq = pair.Key;
                var item = pair.Value;
                if (item.Status == ItemStatus.Playing)
                {
                    state.Orphans[OrphanKey(previous, seq)] = item.Audio;
                }
                else if (item.Audio != null)
                {
                    commands.Add(PlaybackCommand.DiscardAudio(previous, seq, item.Audio));
                }
                state.Items.Remove(seq);
                TrackDelete(state, seq);
            }

            state.Seen.Clear();
            state.SeenOrder.Clear();
            state.MaxSeqConsumed = 0;
            state.PendingAck = null;
            state.HeadSeq = null;
            state.StallWarnedAt = 0;
            // ドライバ側の間引きバッファに残っている旧エポックの ack も落とす
            commands.Add(PlaybackCommand.DropPendingAck());
        }

        private static void OnReceived(PlaybackState state, SpeechFrame record, List<PlaybackCommand> commands)
        {
            if (record == null) return;
            var seq = record.Seq;

            // ★ 採番のやり直しは**契約が運んでくる**。「seq が戻ったのに ts は進んだ」といった
            //   推論はしない（Seen の溢れと取り違える / 同一メッセージ内で ts が同値になる性質と
            //   噛み合わない）。判定は epoch の不一致1本にする
            if (state.EpochId != null && record.Epoch != state.EpochId) ResetEpoch(state, commands);
            state.EpochId = record.Epoch;

            if (state.Seen.Contains(SeenKey(record)))
            {
                // 消費済みのものが再送された ＝ サーバー側にまだ entry が残っている。
                // ack が届く前に切断された / サーバー再起動で配信済みの記憶が消えた、のどちらか。
                // ack を打ち直さないと永久に残る
                EmitAck(state, seq, commands);
                return;
            }

            // 処理中のものの再送。同じ世代の同じ seq は同じ文なので、取得をやり直す意味が無い
            if (state.Items.ContainsKey(seq)) return;

            if (seq <= state.MaxSeqConsumed)
            {
                // ★ 消費済みの範囲なのに Seen に無い ＝ 上限から溢れたキーの再送。
                //   ここを ResetEpoch に落とすと、追いつきが SeenCapacity を超えるたびに
                //   状態を捨てて**同じ文を2回喋る**
                EmitAck(state, seq, commands);
                return;
            }

            state.Items[seq] = new QueueItem
            {
                Record = record,
                Status = ItemStatus.Pending,
                Audio = null,
                Attempts = 0,
                RetryAfter = 0,
            };
            TrackInsert(state, seq);

            // ここまで来たなら、このフレームは今のエポックのもの。溜めてある ack を出してよい
            FlushPendingAck(state, commands);
        }
    }
}
