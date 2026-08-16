/**
 * 1メッセージ分の delta を結合して、発話する文の列にする。chatter-agent の中核。
 *
 * ★ **`final:true` を待ってから呼ぶこと**（CLAUDE.md「絶対に守ること」1 / [#30]）。
 *   メッセージが閉じるまで1文も出さないので、この関数は「メッセージ全文が揃った状態」しか
 *   受け取らない。呼び出し側のゲートは `worker.ts` の `processMessage` にある。
 *
 * 純粋関数であることが重要で、CLI は毎 delta 起動して終了する。ディスクに進捗を持たず、
 * `final` を見たときにゼロから組み直す。
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
 */

import { cleanTextForSpeech, splitIntoSentences } from "../text/textFilter";
import { truncateAtUnstableTail } from "../text/unstableTail";

/**
 * 全文を組み立て、発話する文を順に返す。
 *
 * 読み上げたくない末尾（未閉じの ``` や書きかけの表の行）を先に切り落としてから整形する。
 * → `src/text/unstableTail.ts`
 *
 * @param deltas index 順に並んだ delta。欠番があってはならない（呼び出し側が連続した前半だけを渡す）
 */
export function assembleSentences(deltas: string[]): string[] {
  const safe = truncateAtUnstableTail(deltas.join(""));

  // splitIntoSentences は区切り情報として空文字を残す仕様なので、ここで落とす
  return splitIntoSentences(cleanTextForSpeech(safe)).filter((sentence) => sentence.length > 0);
}
