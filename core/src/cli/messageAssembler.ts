/**
 * delta の集合から「確定した文」だけを切り出す。chatter-agent の中核。
 *
 * ★ `final:true` を待ってはいけない（CLAUDE.md「絶対に守ること」1 / 設計書 §2-4）。
 *   最終チャンクは実測で 34〜80 秒遅れて届く。メッセージが閉じるとき＝次のツール呼び出しが
 *   始まるときに flush され、その手前の thinking を待つため。
 *
 * そこで delta が届くたびに全体を組み直し、**最後の文を除いた**未出力分だけを流す。
 * 最後の文はまだ伸びうるので保留する。
 *
 * この関数が純粋であることが重要で、CLI は毎 delta 起動して終了するため、
 * 状態は「出力済みの文数」だけをディスクに持ち、テキストは毎回ゼロから組み直す。
 */

import { truncateAtUnclosedFence } from "../text/pendingFence";
import { cleanTextForSpeech, splitIntoSentences } from "../text/textFilter";

export interface AssembleInput {
  /** index 順に並んだ delta。欠番があってはならない（呼び出し側が連続した前半だけを渡す） */
  deltas: string[];
  /** 既に speech.jsonl に書いた文の数 */
  emitted: number;
  /**
   * 保留している最後の文も出すか。
   * - `final:true` を処理し終えたとき
   * - **後続のイベントが到着していて、このメッセージがもう伸びないと分かったとき**
   *   （設計書からの上積み。順序を保ったまま 34〜80 秒の遅延を消す）
   */
  flushPending: boolean;
}

export interface AssembleResult {
  /** 今回 speech.jsonl に書くべき文。1文1行になる */
  sentences: string[];
  /** 次回に渡す「出力済みの文数」 */
  emitted: number;
}

/**
 * 全文を組み直し、確定した文のうち未出力のものを返す。
 *
 * 未閉じの ``` 以降を先に切り落とすのが要で、これにより
 * 「開いたままのコードが読み上げられない」と「既に出した文が後から変化しない」が
 * 同時に成立する。後者が `emitted`（文数）で進捗を持てる根拠になっている。
 */
export function assembleSentences(input: AssembleInput): AssembleResult {
  const raw = input.deltas.join("");
  const safe = truncateAtUnclosedFence(raw);
  const cleaned = cleanTextForSpeech(safe);

  // splitIntoSentences は区切り情報として空文字を残す仕様なので、ここで落とす
  const all = splitIntoSentences(cleaned).filter((sentence) => sentence.length > 0);

  const limit = input.flushPending ? all.length : Math.max(0, all.length - 1);
  const emitted = Math.max(input.emitted, limit);

  return {
    sentences: input.emitted < limit ? all.slice(input.emitted, limit) : [],
    emitted,
  };
}
