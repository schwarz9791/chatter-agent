/**
 * 記録（speechLog）と配信キュー（speechQueue）の両方に書く合成。
 *
 * ★ `append` が seq を採番して speech.jsonl に書いた時点で「出した」が確定する。
 *   ここから先（enqueue / trim）で throw させてはいけない。throw すると worker.ts の
 *   processMessage が `removeEntry` より前に抜け、spool の entry がまだ処理していない
 *   ように見える。次の CLI が同じ文を組み直して別の seq を振り直し、speech.jsonl に
 *   同じ文が二重に残る（クライアントの seq 重複排除は seq が違うので効かない）。
 *   enqueue / trim の失敗は記録して黙って進む。
 *
 * ★ **被害範囲はメッセージ全体。** 発話の粒度をメッセージ単位にした（[#30]）ので、
 *   ここで throw したときに組み直されるのは1文ではなく**メッセージ全文**になる。
 *   数十文がまとめて二度読み上げられる。この関数の「append の後は throw しない」は、
 *   その分だけ以前より重い保証になっている。
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
 */

import type { SpeechEntry, SpeechLog } from "../core/speechLog";
import type { SpeechQueue } from "../core/speechQueue";
import type { SpeechRecord } from "../core/types";

export interface PublisherDeps {
  speechLog: SpeechLog;
  speechQueue: SpeechQueue;
  /** config は参照のたびに mtime スタンプを見て読み直す作りなので、起動時の1回きりの値では渡さない */
  maxEntries: () => number;
}

/** 記録と配信の両方に書く。記録できた時点で「出した」が確定する */
export function createPublisher(deps: PublisherDeps): (entries: SpeechEntry[]) => SpeechRecord[] {
  const { speechLog, speechQueue, maxEntries } = deps;

  return (entries) => {
    // ここで throw したら記録も配信も無い。呼び出し側（drainSpool）に throw させて構わない
    const records = speechLog.append(entries);

    try {
      const written = speechQueue.enqueue(records);
      if (written !== records.length) {
        console.error(
          `[chatter-agent-speak] 配信キューへの書き込みが ${records.length} 件中 ${written} 件しか成功しませんでした`,
        );
      }
    } catch (err) {
      // hook 経路では stderr が握り潰される可能性が高いが、手動実行時の手がかりに残す
      console.error("[chatter-agent-speak] 配信キューへの書き込みに失敗しました:", err);
    }

    try {
      // クライアントが繋がっていなければ ack は来ないので、書いた側が歯止めをかける。
      // 捨てた件数はどのログにも出ないと seq の飛びとしてしか症状が出ないので、ここで出す
      const dropped = speechQueue.trim(maxEntries());
      if (dropped > 0) {
        console.error(`[chatter-agent-speak] 配信キューの上限を超えたため ${dropped} 件を破棄しました`);
      }
    } catch (err) {
      console.error("[chatter-agent-speak] 配信キューの上限チェックに失敗しました:", err);
    }

    return records;
  };
}
