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
 * ★ 整形（クリーニング・文分割・不安定末尾の切り落とし）の本体は `../text/speechText.ts` に
 *   移した（issue #38 レビュー A2）。`cli/` からも `summarizer/` からも参照する必要があり、
 *   `summarizer/ → cli/` の依存を作らないためにそちらへ置いてある。ここでの責務は
 *   「delta の結合」だけの薄い adapter（delta 結合の意味を持つ関数名を維持するため、
 *   `toSpeechSentences` をそのまま呼ぶのではなくこの関数を残してある）。
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
 */

import { toSpeechSentences, type SpeechSentencesOptions } from "../text/speechText";

export type AssembleSentencesOptions = SpeechSentencesOptions;

/**
 * 全文を組み立て、発話する文を順に返す。
 *
 * @param deltas index 順に並んだ delta。欠番があってはならない（呼び出し側が連続した前半だけを渡す）
 */
export function assembleSentences(deltas: string[], options: AssembleSentencesOptions = {}): string[] {
  return toSpeechSentences(deltas.join(""), options);
}
