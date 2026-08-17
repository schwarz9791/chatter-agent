/**
 * メッセージ全文（1本の文字列）を、発話する文の列に整形する。
 *
 * ★ **`text/` に置く理由**（PR #38 レビュー A2）: `cli/`（`messageAssembler.ts` の delta 結合の
 *   直後、`worker.ts` の要約フォールバックの直後）からも `summarizer/`（`summaryPipeline.ts` の
 *   受理判定）からも参照する必要がある。`cli/` は既に `summarizer/`（`Summarize` 型）に依存する
 *   形があるので、`summarizer/ → cli/` という逆向きの依存を作ると循環になる。どちらからも見える
 *   `text/` に置けば、この依存関係を作らずに済む。
 *
 * 整形の順序（旧 `cli/messageAssembler.ts` から移設。issue #38 A2）:
 *   1. `truncateAtUnstableTail` で読み上げたくない末尾（未閉じの ``` や書きかけの表の行）を
 *      先に切り落とす → `./unstableTail.ts`
 *   2. `cleanTextForSpeech` で Markdown 等を除去し、`splitIntoSentences` で文に割る
 *      → `./textFilter.ts`
 *   3. 救済経路（`dropUnterminatedTail`）なら、文として閉じていない末尾の1文を落とす
 *
 * ★★ **この関数は冪等ではない。** `cleanTextForSpeech` の10段の正規表現は、インラインコードの
 *   バッククォート除去（段10）が最後にあるため、バッククォートで囲まれた `## 見出し` /
 *   `- item` / `| a | b |` は1パス目で中身が露出し、2パス目で段3/5/7 が消してしまう
 *   （実測: `` `|a|` `` は1パス目で `|a|`、2パス目で空文字列）。**発話に載る値には必ず
 *   1回だけ適用すること。** 呼び出し箇所をここへ集約したのはこの非冪等性を構造的に無関係に
 *   するため（`cli/worker.ts` の `processMessage` / `summarizeSentences` を参照）。
 */

import { cleanTextForSpeech, splitIntoSentences } from "./textFilter";
import { truncateAtUnstableTail } from "./unstableTail";

/** 文として閉じているとみなす末尾（句点・感嘆符・疑問符・改行） */
const SENTENCE_END_RE = /[。！？!?\n\r]\s*$/;

export interface SpeechSentencesOptions {
  /**
   * true のとき、末尾が文として閉じていなければ最後の1文を落とす。
   *
   * `final` を待たない救済経路（`cli/worker.ts` の `hasNewer`）専用。救済経路は「続きが来ないと
   * 確定した」わけではなく「次のイベントが来たので打ち切った」だけなので、割れた文の断片
   * （`["Aの一文目。", "Aの途中"]` のような）をそのまま発話すると不自然な発話になる。
   * `final:true` 経由（もう続きは来ない）では渡さないこと。閉じていない末尾も含めて全部出す。
   *
   * ★ 判定は `truncateAtUnstableTail` を通した後のテキスト（`safe`）に対して行う。
   *   `cleanTextForSpeech` / `splitIntoSentences` を通すと末尾の記号が変形・除去されうるため。
   */
  dropUnterminatedTail?: boolean;
}

/**
 * 発話する文の列を返す。
 *
 * @param text 整形前の全文（Markdown 等が残った状態でよい）
 */
export function toSpeechSentences(text: string, options: SpeechSentencesOptions = {}): string[] {
  const safe = truncateAtUnstableTail(text);

  // splitIntoSentences は区切り情報として空文字を残す仕様なので、ここで落とす
  const sentences = splitIntoSentences(cleanTextForSpeech(safe)).filter((sentence) => sentence.length > 0);

  if (options.dropUnterminatedTail && sentences.length > 0 && !SENTENCE_END_RE.test(safe)) {
    sentences.pop();
  }

  return sentences;
}
