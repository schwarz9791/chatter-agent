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
 * ★★ [#32] のレビューで、上の「開始位置」の探し方が2つとも壊れていた（実測で再現済み）:
 *
 * - 表の行（`incompleteTableRowAt`）は**文字列全体の最後の行しか見ていなかった**ので、
 *   メッセージが改行で終わる（＝最後の行が空文字になる）と検出できず、生パイプがそのまま
 *   読み上げに漏れていた。**全行を見る**ように直した。代償は下記のドキュメントを参照
 * - コードフェンス（`unclosedFenceAt`）は ``` の出現回数を**行頭かどうか無関係に**数えていた
 *   ので、地の文に混ざった ``` （「バッククォート \`\`\` を使います」のような文中の引用）が
 *   奇数個目に化けると、そこから末尾までが丸ごと無音になっていた。**行頭の ``` だけを
 *   開始として数える**ように直した。代償は `unclosedFenceAt` のコメントを参照
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
 * [#32]: https://github.com/schwarz9791/chatter-agent/issues/32
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
 * ★ [#32] 修正前は `cleanTextForSpeech` の正規表現（`/```[\s\S]*?```/g`）に合わせて、
 *   行頭かどうかを見ずに ``` を左から非重複で拾い、奇数個目を開き・偶数個目を閉じとして
 *   扱っていた。これだと地の文の中に ``` が1つ紛れ込む（「バッククォート \`\`\` を
 *   使います」のような文中の言及）だけで開閉が反転し、**そこから末尾までを丸ごと切り
 *   落としてしまう**（実測で確認済み。[#32]）。
 *
 *   守りたい不変条件は2つあり、両立しない:
 *     1. コードが読み上げに漏れない（未閉じフェンス以降を切る理由）
 *     2. 地の文が無言で消えない（切りすぎない理由）
 *
 *   ★ ここでは 2 を優先し、**開き側は行頭（先頭の空白は許す）の ``` だけを開始として
 *   数える**ことにした。実際のコードフェンスはほぼ必ず行頭から始まる一方、地の文の中で
 *   ``` に言及するときは行の途中に出てくるので、行頭限定は開き側の誤検出をほぼ無くせる。
 *
 *   代償は2つ:
 *   - **`cleanTextForSpeech` の数え方とズレる。** あの正規表現は行頭を見ないので、地の文の
 *     迷子の ``` が実際のコードフェンスと誤って対にされることが理論上ありうる。ただし
 *     `cleanTextForSpeech` は非貪欲マッチなので、対になる相手が見つからなければその ```
 *     はただの文字として残るだけ（読み上げエンジンは記号を発音しないので実害は小さい。
 *     CLAUDE.md 実測ノート参照）。「コードが漏れる」よりずっと軽い失敗モード
 *   - **閉じ側は行頭を要求しない**（既存仕様のまま）。すでに開いている状態で見つかった
 *     ``` は無条件で閉じ扱いにする。閉じ側まで行頭限定にすると `説明。\n```````
 *     （開閉が同じ行に連続する）のような既存仕様を壊すため。閉じ探索は「開いている
 *     区間の中」でしか働かないので、地の文の迷子フェンスが誤って閉じ役にされるのは
 *     「本物のコードフェンスが開いた直後」に限られ、影響範囲は開き側よりずっと狭い
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
    } else if (isAtLineStart(text, found)) {
      isOpen = true;
      openedAt = found;
    }
    // 行頭でない ``` は開始候補にせず読み飛ばす（地の文の迷子フェンス対策。上のコメント参照）
    searchFrom = found + FENCE.length;
  }

  return isOpen ? openedAt : null;
}

/**
 * `index` の直前が「行頭（先頭の空白・タブは許す）」かどうか。
 * 改行 / 復帰 / 文字列先頭まで戻って非空白文字に当たらなければ true。
 */
function isAtLineStart(text: string, index: number): boolean {
  let i = index - 1;
  while (i >= 0 && (text[i] === " " || text[i] === "\t")) i--;
  return i < 0 || text[i] === "\n" || text[i] === "\r";
}

/**
 * 書きかけの表の行の位置。
 *
 * 除去の正規表現は `/^\|.*\|$/gm` で、行が `|` で閉じて初めて消える。閉じていない行は
 * 生の `| A | B` が1文として読み上げられてしまう。
 *
 * ★ [#32] 修正前は `scan.lastIndexOf("\n") + 1` で**文字列全体の最後の行しか**見て
 *   いなかった。メッセージが改行で終わる（＝最後の行が空文字になる）と、その手前にある
 *   未閉じの表の行を素通りしてしまい、生パイプがそのまま読み上げに漏れていた（実測で
 *   確認済み。[#32]）。**全行を走査**し、`|` で始まるのに `/^\|.*\|$/` に一致しない
 *   最初の行の開始位置を返すように直した。
 *
 *   ★★ 代償: 未閉じの表の行が本文の途中にあると、**そこから末尾までを丸ごと切り落とす**
 *   （最初に見つかった不安定行より後ろは、本物の地の文であっても失われる）。表の行1つを
 *   誤読み上げさせないために、その後ろの文をまるごと諦める形になる。「生パイプを読み上げる」
 *   より「その先の文が発話されない」方が実害が小さいと判断してこちらを選んだ
 *   （読み上げ事故＝ユーザーに見える形で表構文がそのまま音になる、の方が気付きやすく、
 *   目に見える表示（`MessageDisplay` 側）とは独立に発話だけが欠けるのは実害としては軽い）
 */
function incompleteTableRowAt(scan: string): number | null {
  let lineStart = 0;

  while (lineStart <= scan.length) {
    const newlineAt = scan.indexOf("\n", lineStart);
    const lineEnd = newlineAt === -1 ? scan.length : newlineAt;
    const line = scan.slice(lineStart, lineEnd);

    if (line.startsWith("|") && !/^\|.*\|$/.test(line)) {
      return lineStart;
    }

    if (newlineAt === -1) break;
    lineStart = newlineAt + 1;
  }

  return null;
}
