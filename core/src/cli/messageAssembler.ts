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

import { cleanTextForSpeech, splitIntoSentences } from "../text/textFilter";
import { truncateAtUnstableTail } from "../text/unstableTail";

export interface AssembleInput {
  /** index 順に並んだ delta。欠番があってはならない（呼び出し側が連続した前半だけを渡す） */
  deltas: string[];
  /** 既に speech.jsonl に書いた文の数 */
  emitted: number;
  /**
   * `final:true` を処理した。もう delta は増えない。
   *
   * ★ `flushPending` と別に持つこと。後続イベントによる保留解除では**まだテキストが伸びる**ので、
   *   未確定の末尾（未閉じの `<` など）を確定扱いしてはいけない。
   */
  final: boolean;
  /**
   * 保留している最後の文も出すか。
   * - `final:true` を処理したとき
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

/** 文として閉じているか。句点・感嘆符・疑問符か、行が変わっていれば閉じている */
function endsAtBoundary(text: string): boolean {
  return text.length === 0 || /[。！？!?\n\r]\s*$/.test(text);
}

/**
 * 行として閉じているか。**まだ delta が続くときの保留を外してよいかの判定。**
 *
 * ★ `MessageDisplay` の delta は「最後の flush を除いて必ず行単位」で届く
 *   （Claude Code のスキーマ記述 / 実測でも非 final の delta は全て改行で終わっていた）。
 *   `splitIntoSentences` は改行でも分割するので、蓄積テキストが行として閉じていれば
 *   **最後の文はもう伸びない**。保留する理由が無い。
 *
 * ★ 句点（`。！？`）だけでは足りないので `endsAtBoundary` を流用しないこと。
 *   `truncateAtUnstableTail` が行の途中で切ると句点で終わりうるが、その先は次の delta で伸びる。
 *
 * これを入れる前は、段落ごとに**最後の1文だけが次の delta まで待って**いた。
 * 実測で 1.4〜5.7 秒（`final` 直前の段落では最大）。
 */
function endsAtLineBoundary(text: string): boolean {
  return /[\n\r]\s*$/.test(text);
}

/**
 * 全文を組み直し、確定した文のうち未出力のものを返す。
 *
 * 未確定の末尾（未閉じの ``` や `<` など）を先に切り落とすのが要で、これにより
 * 「開いたままのコードや表が読み上げられない」と「既に出した文が後から変化しない」が
 * 同時に成立する。後者が `emitted`（文数）で進捗を持てる根拠になっている。
 */
export function assembleSentences(input: AssembleInput): AssembleResult {
  const raw = input.deltas.join("");
  const safe = truncateAtUnstableTail(raw, { final: input.final });
  const cleaned = cleanTextForSpeech(safe);

  // splitIntoSentences は区切り情報として空文字を残す仕様なので、ここで落とす
  const all = splitIntoSentences(cleaned).filter((sentence) => sentence.length > 0);

  const limit = resolveLimit(all.length, input, safe);

  // ★ 高水位で固定しないこと。整形結果が縮んだときに emitted が張り付くと、
  //   slice が永久に空を返して**以降のすべての文が発話されなくなる**。
  //   末尾の保留で縮み自体を稀にしたうえで、それでも起きたら現実の長さへ追従する。
  const clamped = Math.min(input.emitted, all.length);
  const from = Math.min(clamped, limit);

  return {
    sentences: all.slice(from, limit),
    emitted: Math.max(clamped, limit),
  };
}

function resolveLimit(total: number, input: AssembleInput, safe: string): number {
  if (input.final) return total;

  // 行が閉じているなら、まだ delta が続いていても最後の文は確定している
  if (endsAtLineBoundary(safe)) return total;

  // 後続イベントによる保留解除。まだ final は届いていないので、
  // **文として閉じていない断片は出さない**（途中まで読み上げると聞き手には事故に聞こえる）
  if (input.flushPending && endsAtBoundary(safe)) return total;

  return Math.max(0, total - 1);
}
