/**
 * キューを見て「まだ配信していないものは何か」を決める部品。
 *
 * `server/` は判断ロジックを持たない方針（docs/core.md）だが、この判断──何を配信済みとし、
 * 何を消してよいか──はレビューの指摘が集中した箇所だった。`server/index.ts` の `main()` に
 * 埋めたままだとユニットテストから触れないので、純粋な部品として切り出す。
 */

import type { SpeechQueue } from "../core/speechQueue";
import type { SpeechEpoch } from "../core/types";

export interface DispatcherDeps {
  queue: SpeechQueue;
  /** 接続中の全クライアントへ流す */
  broadcast: (line: string) => void;
}

export interface Dispatcher {
  /** キューを見て、まだ配信していないものを流す */
  poll(): void;
  /** 接続直後の追いつき。配信済みのものだけを送る */
  catchUp(send: (line: string) => boolean): void;
  /**
   * クライアントの累積 ack。
   *
   * `epoch` が `null` なら「世代を名乗らない ack」で、現在の世代のものとして扱う
   * （契約上 `epoch` は任意フィールド → docs/protocol.md）。
   */
  ack(seq: number, epoch?: SpeechEpoch | null): void;
}

export function createDispatcher(deps: DispatcherDeps): Dispatcher {
  /**
   * 配信済みの seq の集合。
   *
   * ★ 単調増加の水位（旧 `sentUpTo`）ではなく集合で持つ。水位だと、キューの採番が
   *   1からやり直された（`~/.config/chatter-agent` の削除やバックアップ復元）ときに、
   *   新しい seq が古い水位を下回ってしまい**永久に配信されなくなる**。
   *
   *   集合はキューの上限（既定 500）で頭打ちなので、水位ひとつと比べたコストの差は
   *   無視できる。
   */
  const delivered = new Set<number>();

  /**
   * 世代違いで配信を見送った seq。
   *
   * `delivered` と同じく、キューから消えたぶんを毎 poll で落とす。これを持たないと
   * 20回/秒のポーリングのたびに同じファイルを読み直すことになる。
   */
  const skipped = new Set<number>();

  /**
   * 今配信している採番の世代。最初に読めた entry の `epoch` で決まる。
   *
   * ★ **本来ここは1つしか無いはず。** 採番がやり直されたときは、ロックを持っている
   *   書き手（CLI）が `speechQueue.clear()` で旧世代を捨てる（`cli/publish.ts`）。
   *   ここにあるのは、CLI を経由しない経路（バックアップの復元、手で置いたファイル）に
   *   対する安全網。
   */
  let currentEpoch: SpeechEpoch | null = null;
  /** 現世代で見た最大の `ts`。世代を乗り換えてよいかの判定に使う */
  let maxTsSeen = "";
  /** 一度警告した世代。20回/秒のポーリングと ack のたびに同じ行を出さないため */
  const warnedEpochs = new Set<string>();

  function warnOnce(epoch: string, message: string): void {
    if (warnedEpochs.has(epoch)) return;
    warnedEpochs.add(epoch);
    console.warn(message);
  }

  /** 世代を乗り換える。旧世代について覚えていたことを全部捨てる */
  function switchEpoch(epoch: SpeechEpoch, ts: string): void {
    currentEpoch = epoch;
    maxTsSeen = ts;
    delivered.clear();
    skipped.clear();
  }

  return {
    poll() {
      const seqs = deps.queue.list();

      // キューから消えた分（ack / trim / 起動時の掃除）を集合から落とす。
      // 集合の大きさをキューの上限で頭打ちにすると同時に、採番がやり直されたときに
      // 同じ seq を「配信済み」と誤認しないための後始末でもある
      const present = new Set(seqs);
      for (const seq of delivered) if (!present.has(seq)) delivered.delete(seq);
      for (const seq of skipped) if (!present.has(seq)) skipped.delete(seq);

      for (const seq of seqs) {
        if (delivered.has(seq) || skipped.has(seq)) continue;

        const entry = deps.queue.read(seq);
        if (entry === null) {
          // 読めない entry は配信できない（protocol.md の「SpeechRecord の JSON 以外は
          // 送らない」を破れない）。配信済み扱いにしておかないと、毎 poll（20回/秒）
          // 同じ entry を見て警告し続ける
          console.warn(`[Server] seq=${seq} を読めなかったのでスキップします`);
          delivered.add(seq);
          continue;
        }

        const { line, record } = entry;

        if (currentEpoch === null) {
          switchEpoch(record.epoch, record.ts);
        } else if (record.epoch !== currentEpoch) {
          // ★ 「どちらの世代が新しいか」はファイル名では決まらない。採番のやり直し直後の
          //   キューは `1(新) 2(新) … 400(旧)` になり、list() は seq 昇順なので**新しい方が
          //   先頭に来る**。判定できるのは `ts` だけ。
          if (record.ts <= maxTsSeen) {
            warnOnce(
              record.epoch,
              `[Server] 旧世代（epoch=${record.epoch}）の entry は配信しません。古くなれば起動時の掃除で消えます`,
            );
            skipped.add(seq);
            continue;
          }
          warnOnce(record.epoch, `[Server] 採番のやり直しを検出しました（epoch=${record.epoch}）`);
          switchEpoch(record.epoch, record.ts);
        }

        if (record.ts > maxTsSeen) maxTsSeen = record.ts;

        deps.broadcast(line);
        delivered.add(seq);
      }
    },

    catchUp(send) {
      let sent = 0;
      let truncated = false;

      // 配信済みのものだけを seq 昇順に送る。queue.list() が昇順を返すので、
      // delivered（Set）側を別途ソートする必要はない
      for (const seq of deps.queue.list()) {
        if (!delivered.has(seq)) continue;

        const entry = deps.queue.read(seq);
        if (entry === null) continue; // 追いつきの走査中に ack 等で消えた

        if (!send(entry.line)) {
          truncated = true;
          break;
        }
        sent++;
      }

      if (truncated) {
        // 旧文言（「最後の seq から接続し直すこと」）は ?since= の廃止で実行不能。
        // バックプレッシャでは wsServer 側が接続を切るので、繋ぎ直せば未 ack 分が
        // 接続直後の追いつきで再送される、というのが今の実態
        console.warn(`[Server] 追いつきを ${sent} 件で打ち切りました。繋ぎ直せば未 ack 分が再送されます`);
      } else if (sent > 0) {
        console.log(`[Server] 未読 ${sent} 件を送りました`);
      }
    },

    ack(seq, epoch = null) {
      // ★ 旧世代の ack を通さない。累積 ack は `seq <= upTo` を**ファイル名で範囲削除**するので、
      //   採番がやり直された直後に旧世代の ack（例: 500）が届くと、まだ喋っていない
      //   新しい seq 1, 2 がまとめて消える。以前はクライアント側がこれを自衛していたが、
      //   その自衛は「open では保留 ack を送らない」という約40行の状態機械を要求していた。
      //
      // ★ 黙って捨てないこと。捨てられた ack はキューを減らさないので、症状は
      //   「上限まで溜まって古い方から捨てられ続ける」になり、エラーが1行も出ない
      if (epoch !== null && currentEpoch !== null && epoch !== currentEpoch) {
        warnOnce(`ack:${epoch}`, `[Server] 旧世代（epoch=${epoch}）の ack は無視します。現在は epoch=${currentEpoch}`);
        return;
      }

      // ★ クライアント由来の値。配信済みの範囲に頭を押さえること。`parseAck` は形
      //   （非負の安全整数）しか見ないので、ここで押さえないと `Number.MAX_SAFE_INTEGER`
      //   のような値が通り、まだ配信していない entry まで含めてキューを全消しされる
      let upTo = 0;
      for (const s of delivered) if (s <= seq && s > upTo) upTo = s;
      if (upTo === 0) return;

      const removed = deps.queue.ackUpTo(upTo);
      if (removed > 0) console.log(`[Server] seq<=${upTo} を ${removed} 件消しました`);
    },
  };
}
