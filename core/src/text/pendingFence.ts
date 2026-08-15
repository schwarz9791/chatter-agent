/**
 * 未閉じコードフェンスの保留。
 *
 * chatter-agent 固有の要件で、上流 cc-mascot には存在しない。
 *
 * `cleanTextForSpeech` のコードブロック除去は ```` /```[\s\S]*?```/g ```` で、
 * **閉じフェンスが揃って初めて**ブロックを消す。ストリーミング中の raw には
 * 開いたままの ``` が普通に現れるので、そのまま流すとコードが読み上げられる。
 *
 * 開いたままのフェンスより後ろを切り落としてから整形すれば、
 *
 * - 未閉じの間はコードが漏れない
 * - フェンスが閉じた瞬間にブロック全体が消えても、**既に出した文は変化しない**
 *
 * の両方が同時に成立する。後者は「出力済みの文数」で進捗を持つ設計の前提条件になっている。
 */

const FENCE = "```";

/**
 * 開いたままのコードフェンスがあれば、その開始位置より後ろを切り落とす。
 * フェンスが揃っていれば元の文字列をそのまま返す。
 *
 * フェンスの数え方は `cleanTextForSpeech` の正規表現に合わせ、
 * 左から順に非重複で ``` を拾い、奇数個目を開き・偶数個目を閉じとして扱う。
 * 行頭かどうかは見ない（正規表現も見ていないため）。
 */
export function truncateAtUnclosedFence(text: string): string {
  let searchFrom = 0;
  let openedAt = -1;
  let isOpen = false;

  for (;;) {
    const found = text.indexOf(FENCE, searchFrom);
    if (found === -1) break;

    if (isOpen) {
      isOpen = false;
    } else {
      isOpen = true;
      openedAt = found;
    }
    searchFrom = found + FENCE.length;
  }

  return isOpen ? text.slice(0, openedAt) : text;
}
