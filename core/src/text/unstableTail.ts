/**
 * 読み上げたくない末尾の切り落とし。
 *
 * chatter-agent 固有の要件で、上流 cc-mascot には存在しない。
 *
 * `cleanTextForSpeech` の10段の正規表現には、**閉じ側が来て初めて除去が効く**ものがある。
 * 開いたままだと除去が空振りし、中身が生のまま読み上げに漏れる:
 *
 * | 構文 | 何が起きるか |
 * |---|---|
 * | ```` ``` ```` | 閉じフェンスが無いとコードがそのまま読み上げられる |
 * | 表の行 | 行が `\|` で閉じていないと生の `\| A \| B` が読み上げられる |
 *
 * これらの開始位置より後ろを切り落としてから整形すれば、どちらも漏れない。
 *
 * ★ 引き換えに、切り落とした分は**発話されない**。`final:true` を待って1回だけ組み立てる
 *   ようになった（[#30]）ので「後から届いて閉じる」ことはもう無く、未閉じのまま終わった
 *   コードや表はそのまま捨てる。読み上げたくないものなので、これでよい。
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
 */

const FENCE = "```";

/**
 * 読み上げたくない末尾があれば、その開始位置より後ろを切り落とす。
 * すべて閉じていれば元の文字列をそのまま返す。
 */
export function truncateAtUnstableTail(text: string): string {
  // ★ 閉じたコードブロックの中身は評価対象から外す。これが無いと `incompleteTableRowAt` が
  //   コードブロック内の `|` 行に反応する。オフセットを保ちたいので、改行以外を空白に潰して
  //   長さを変えない
  const scan = text.replace(/```[\s\S]*?```/g, (block) => block.replace(/[^\n]/g, " "));

  let cut = text.length;
  for (const at of [unclosedFenceAt(text), incompleteTableRowAt(scan)]) {
    if (at !== null && at < cut) cut = at;
  }

  return cut === text.length ? text : text.slice(0, cut);
}

/**
 * 開いたままのコードフェンスの開始位置。
 *
 * 数え方は `cleanTextForSpeech` の正規表現に合わせ、左から順に非重複で ``` を拾い、
 * 奇数個目を開き・偶数個目を閉じとして扱う。行頭かどうかは見ない（正規表現も見ていない）。
 */
export function unclosedFenceAt(text: string): number | null {
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

  return isOpen ? openedAt : null;
}

/**
 * 書きかけの表の行の位置。
 *
 * 除去の正規表現は `/^\|.*\|$/gm` で、行が `|` で閉じて初めて消える。閉じていない行は
 * 生の `| A | B` が1文として読み上げられてしまう。
 */
function incompleteTableRowAt(scan: string): number | null {
  const lineStart = scan.lastIndexOf("\n") + 1;
  const line = scan.slice(lineStart);

  if (!line.startsWith("|")) return null;
  return /^\|.*\|$/.test(line) ? null : lineStart;
}
