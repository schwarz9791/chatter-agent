/**
 * キューを見て「まだ配信していないものは何か」を決める部品。
 *
 * `server/` は判断ロジックを持たない方針（docs/core.md）だが、この判断──何を配信済みとし、
 * 何を消してよいか──はレビューの指摘が集中した箇所だった。`server/index.ts` の `main()` に
 * 埋めたままだとユニットテストから触れないので、純粋な部品として切り出す。
 */

import type { SpeechQueue } from "../core/speechQueue";

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
  /** クライアントの累積 ack */
  ack(seq: number): void;
}

export function createDispatcher(deps: DispatcherDeps): Dispatcher {
  /**
   * 配信済みの seq の集合。
   *
   * ★ 単調増加の水位（旧 `sentUpTo`）ではなく集合で持つ。水位だと、キューの採番が
   *   1からやり直された（`~/.config/chatter-agent` の削除やバックアップ復元）ときに、
   *   新しい seq が古い水位を下回ってしまい**永久に配信されなくなる**。
   *
   *   古い未 ack の entry がキューに残ったまま採番がやり直された場合はさらに悪い。
   *   「キューの最大 seq が水位を下回ったらリセット」という水位1本の修正でも直らない
   *   （最大値は古い entry のままなので判定が発火しない。しかも `trim` は新しく書かれた
   *   低い seq から先に捨てるので、この状態から自然に抜け出せない）。
   *
   *   集合はキューの上限（既定 500）で頭打ちなので、水位ひとつと比べたコストの差は
   *   無視できる。
   */
  const delivered = new Set<number>();

  return {
    poll() {
      const seqs = deps.queue.list();

      // キューから消えた分（ack / trim / 起動時の掃除）を集合から落とす。
      // 集合の大きさをキューの上限で頭打ちにすると同時に、採番がやり直されたときに
      // 同じ seq を「配信済み」と誤認しないための後始末でもある
      const present = new Set(seqs);
      for (const seq of delivered) if (!present.has(seq)) delivered.delete(seq);

      for (const seq of seqs) {
        if (delivered.has(seq)) continue;

        const line = deps.queue.read(seq);
        if (line === null) {
          // 読めない entry は配信できない（protocol.md の「SpeechRecord の JSON 以外は
          // 送らない」を破れない）。配信済み扱いにしておかないと、毎 poll（10回/秒）
          // 同じ entry を見て警告し続ける
          console.warn(`[Server] seq=${seq} を読めなかったのでスキップします`);
          delivered.add(seq);
          continue;
        }

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

        const line = deps.queue.read(seq);
        if (line === null) continue; // 追いつきの走査中に ack 等で消えた

        if (!send(line)) {
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

    ack(seq) {
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
