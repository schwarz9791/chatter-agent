/**
 * まだ確定していない末尾の切り落とし。
 *
 * chatter-agent 固有の要件で、上流 cc-mascot には存在しない。
 *
 * CLI は毎 delta 起動して終了するため、進捗は「出力済みの文数」でしか持てない。
 * これが成り立つ前提は **既に出した範囲が後から変化しないこと** で、`cleanTextForSpeech` を
 * 伸び続ける raw に繰り返し適用するかぎり、その前提は自動では成立しない。
 *
 * 10段の正規表現のうち、**開始位置が既出範囲にあり、閉じ側が後から届く**ものが危険:
 *
 * | 構文 | 何が起きるか |
 * |---|---|
 * | ```` ``` ```` | 閉じフェンスが来るまでコードが読み上げられる |
 * | `<…>` | `>` が届いた瞬間、`<` 以降の**既に発話した文ごと**削除される |
 * | `` `…` `` | 閉じバッククォートが届くと既出テキストから記号が消える |
 * | 表の行 | 行が閉じるまで生の `\| A \| B` が読み上げられ、閉じると消える |
 * | URL | 空白が来るまで削除範囲が伸び続ける |
 * | 16進列 | 7文字目が届いた瞬間に消え、41文字目で戻る |
 *
 * これらの開始位置より後ろを切り落としてから整形すれば、既出範囲は変化しなくなる。
 *
 * ★ 引き換えに**発話が遅れる**。未閉じの `<` がある間、それ以降は保留される。
 *   `final:true` で保留は解ける（もう伸びないので不安定ではなくなる）ため、
 *   遅延の上限は「メッセージが閉じるまで」＝保留中の最終文と同じ。
 */

const FENCE = "```";

export interface TruncateOptions {
  /**
   * メッセージが閉じた（`final:true` を処理した）。もう delta は増えないので、
   * 「伸びることで不安定になる」ものは考えなくてよい。
   *
   * ★ 後続イベントによる保留解除（`flushPending`）ではこれを立てないこと。
   *   そのメッセージの `final` はまだ届いておらず、テキストはまだ伸びる。
   */
  final?: boolean;
}

/**
 * まだ確定していない末尾があれば、その開始位置より後ろを切り落とす。
 * すべて確定していれば元の文字列をそのまま返す。
 */
export function truncateAtUnstableTail(text: string, options: TruncateOptions = {}): string {
  // 閉じたコードブロックの中身は評価対象から外す。オフセットを保ちたいので、
  // 改行以外を空白に潰して長さを変えない
  const scan = text.replace(/```[\s\S]*?```/g, (block) => block.replace(/[^\n]/g, " "));

  // 「そもそも読み上げたくない」もの。final でも切る
  const always = [unclosedFenceAt(text), incompleteTableRowAt(scan)];

  // 「伸びることで既出範囲を変えてしまう」もの。final なら伸びないので切らない
  const whileStreaming = options.final
    ? []
    : [unclosedTagAt(scan), unclosedInlineCodeAt(scan), trailingUrlAt(scan), trailingHexRunAt(scan)];

  let cut = text.length;
  for (const at of [...always, ...whileStreaming]) {
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
  return unclosedDelimiterAt(text, FENCE);
}

/** 開いたままのインラインバッククォートの開始位置 */
function unclosedInlineCodeAt(scan: string): number | null {
  return unclosedDelimiterAt(scan, "`");
}

function unclosedDelimiterAt(text: string, delimiter: string): number | null {
  let searchFrom = 0;
  let openedAt = -1;
  let isOpen = false;

  for (;;) {
    const found = text.indexOf(delimiter, searchFrom);
    if (found === -1) break;

    if (isOpen) {
      isOpen = false;
    } else {
      isOpen = true;
      openedAt = found;
    }
    searchFrom = found + delimiter.length;
  }

  return isOpen ? openedAt : null;
}

/**
 * 閉じていない `<` の位置。
 *
 * `/<[^>]+>/g` は左から非重複で拾うので、**最後の `>` より後ろにある最初の `<`** が
 * 閉じ待ちになる。それより前の `<` はすでにどれかの `>` と対になっている。
 */
function unclosedTagAt(scan: string): number | null {
  const found = scan.indexOf("<", scan.lastIndexOf(">") + 1);
  return found === -1 ? null : found;
}

/**
 * 書きかけの表の行の位置。
 *
 * 除去の正規表現は `/^\|.*\|$/gm` で、行が `|` で閉じて初めて消える。閉じるまでの間は
 * 生の `| A | B` が1文として読み上げられ、閉じた瞬間に消えるので、既出範囲が縮む。
 */
function incompleteTableRowAt(scan: string): number | null {
  const lineStart = scan.lastIndexOf("\n") + 1;
  const line = scan.slice(lineStart);

  if (!line.startsWith("|")) return null;
  return /^\|.*\|$/.test(line) ? null : lineStart;
}

/** 末尾の URL。後続の空白が来るまで削除範囲が伸び続ける */
function trailingUrlAt(scan: string): number | null {
  return scan.match(/https?:\/\/\S*$/)?.index ?? null;
}

/** 末尾の16進列。7文字目が届いた瞬間に消え、41文字目で戻る */
function trailingHexRunAt(scan: string): number | null {
  return scan.match(/\b[0-9a-f]+$/)?.index ?? null;
}
