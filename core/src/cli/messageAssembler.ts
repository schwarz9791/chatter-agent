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

/** 文として閉じているとみなす末尾（句点・感嘆符・疑問符・改行） */
const SENTENCE_END_RE = /[。！？!?\n\r]\s*$/;

export interface AssembleSentencesOptions {
  /**
   * `final` を待たない救済経路（`worker.ts` の `hasNewer`）専用。true のとき、末尾が
   * 文として閉じていなければ最後の1文を落とす。
   *
   * 救済経路は「続きが来ないと確定した」わけではなく「次のイベントが来たので打ち切った」
   * だけなので、割れた文の断片（`["Aの一文目。", "Aの途中"]` のような）をそのまま発話すると
   * 不自然な発話になる（pre-#30 の `resolveLimit` 相当の抑制。CLAUDE.md 承認済み計画 A-3(e)）。
   *
   * `final:true` 経由（`content.final === true`）では渡さないこと。もう続きは来ないので、
   * 閉じていない末尾も含めて全部出す。
   *
   * ★ 判定は `truncateAtUnstableTail` を通した後のテキスト（`safe`）に対して行う。
   *   `cleanTextForSpeech` / `splitIntoSentences` を通すと末尾の記号が変形・除去されうるため。
   */
  dropUnterminatedTail?: boolean;
}

/**
 * 全文を組み立て、発話する文を順に返す。
 *
 * 読み上げたくない末尾（未閉じの ``` や書きかけの表の行）を先に切り落としてから整形する。
 * → `src/text/unstableTail.ts`
 *
 * @param deltas index 順に並んだ delta。欠番があってはならない（呼び出し側が連続した前半だけを渡す）
 */
export function assembleSentences(deltas: string[], options: AssembleSentencesOptions = {}): string[] {
  const safe = truncateAtUnstableTail(deltas.join(""));

  // splitIntoSentences は区切り情報として空文字を残す仕様なので、ここで落とす
  const sentences = splitIntoSentences(cleanTextForSpeech(safe)).filter((sentence) => sentence.length > 0);

  if (options.dropUnterminatedTail && sentences.length > 0 && !SENTENCE_END_RE.test(safe)) {
    sentences.pop();
  }

  return sentences;
}
