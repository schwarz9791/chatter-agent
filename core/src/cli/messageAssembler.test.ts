import { describe, it, expect } from "vitest";
import { assembleSentences } from "./messageAssembler";
import type { AssembleInput } from "./messageAssembler";

function assemble(input: Partial<AssembleInput> & Pick<AssembleInput, "deltas">) {
  return assembleSentences({ emitted: 0, final: false, flushPending: false, ...input });
}

/**
 * delta が1つずつ届く流れを再現し、各ステップで出力された文を集める。
 * `emitted` はディスクに載る進捗と同じ扱いで持ち回る。
 */
function stream(deltas: string[], finalAtEnd = false): { steps: string[][]; total: string[] } {
  const steps: string[][] = [];
  const total: string[] = [];
  let emitted = 0;

  for (let i = 0; i < deltas.length; i++) {
    const final = finalAtEnd && i === deltas.length - 1;
    const result = assemble({ deltas: deltas.slice(0, i + 1), emitted, final, flushPending: final });
    emitted = result.emitted;
    steps.push(result.sentences);
    total.push(...result.sentences);
  }
  return { steps, total };
}

describe("assembleSentences", () => {
  it("最後の文は保留する（まだ伸びうるため）", () => {
    const r = assemble({ deltas: ["確認します。ログを見ます。"] });
    expect(r.sentences).toEqual(["確認します。"]);
    expect(r.emitted).toBe(1);
  });

  it("final で保留していた最後の文も出す", () => {
    const r = assemble({ deltas: ["確認します。ログを見ます。"], emitted: 1, final: true, flushPending: true });
    expect(r.sentences).toEqual(["ログを見ます。"]);
    expect(r.emitted).toBe(2);
  });

  it("文が1つしか無い間は何も出さない", () => {
    const r = assemble({ deltas: ["確認します。"] });
    expect(r.sentences).toEqual([]);
    expect(r.emitted).toBe(0);
  });

  it("空の delta では何も出さない", () => {
    expect(assemble({ deltas: [] }).sentences).toEqual([]);
    expect(assemble({ deltas: [""], final: true, flushPending: true }).sentences).toEqual([]);
  });

  it("同じ文を二度出さない", () => {
    const first = assemble({ deltas: ["あ。い。"] });
    expect(first.sentences).toEqual(["あ。"]);

    const second = assemble({ deltas: ["あ。い。う。"], emitted: first.emitted });
    expect(second.sentences).toEqual(["い。"]);
  });
});

describe("チャンク境界が文の途中で切れても壊れない（設計書 §2-4）", () => {
  it("文の途中で切れた delta を跨いで1文にまとめる", () => {
    const { total } = stream(["確認し", "ます。ログを", "見ます。おわり。"], true);
    expect(total).toEqual(["確認します。", "ログを見ます。", "おわり。"]);
  });

  it("delta ごとに、確定した分だけが順に出る", () => {
    const { steps } = stream(["あ。い", "。う", "。え。"], true);
    expect(steps).toEqual([["あ。"], ["い。"], ["う。", "え。"]]);
  });

  it("改行だけの delta でも重複しない", () => {
    const { total } = stream(["1つ目。\n", "\n2つ目。\n", "\n3つ目。"], true);
    expect(total).toEqual(["1つ目。", "2つ目。", "3つ目。"]);
  });
});

describe("未確定の末尾は保留する（CLAUDE.md 絶対に守ること1）", () => {
  it("開いたままのコードは読み上げない", () => {
    const r = assemble({ deltas: ["こう書きます。\n```ts\nconst secret = 1;\n"], final: true, flushPending: true });
    expect(r.sentences).toEqual(["こう書きます。"]);
  });

  it("フェンスが閉じたらブロックごと消え、手前の文は二度出ない", () => {
    const { total } = stream(["説明します。\n```ts\n", "const a = 1;\n", "```\n以上です。"], true);
    expect(total).toEqual(["説明します。", "以上です。"]);
    expect(total.join("")).not.toContain("const a");
  });

  it("フェンスが閉じないまま final を迎えても、コードは出さない", () => {
    const { total } = stream(["説明します。\n```ts\n", "const a = 1;"], true);
    expect(total).toEqual(["説明します。"]);
  });

  it("書きかけの表の行を生のまま読み上げない", () => {
    const { total } = stream(["手順です。\n| A | B |\n| C | D", " |\n完了です。"], true);
    expect(total).toEqual(["手順です。", "完了です。"]);
    expect(total.join("")).not.toContain("|");
  });
});

/**
 * レビュー #2 の回帰テスト。
 *
 * `cleanTextForSpeech` の結果は伸びるだけでなく**縮む**。`<` が閉じると `/<[^>]+>/g` が
 * 既に発話した範囲ごと削除するため、`emitted` を高水位で固定していると slice が永久に
 * 空を返し、以降のすべての文が発話されなくなっていた。
 */
describe("整形結果が縮んでも文を失わない", () => {
  it("`>` が遅れて届いても、その後ろの文が発話される", () => {
    const { total } = stream(
      ["条件は a < b です。", "まず確認します。次にビルドします。", "順序は c > d です。テストします。完了しました。"],
      true,
    );

    // 高水位固定を直す前は、ここで「テストします。」が永久に発話されなかった
    expect(total).toContain("テストします。");
    expect(total).toContain("完了しました。");

    // `< … >` に挟まれた範囲は cleanTextForSpeech が丸ごと削除する。これは移植した
    // 正規表現の問題（docs/core.md の「既知の欠落」）で、ここでは保留によって
    // **一度発話してから取り消す**事故だけを防いでいる
    expect(total).not.toContain("まず確認します。");
  });

  it("発話済みの文が後から取り消されない（先頭は伸びるだけ）", () => {
    const deltas = ["まず確認します。条件は a < b です。", "次にビルドします。順序は c > d です。テストします。"];
    const spoken: string[] = [];
    let emitted = 0;

    for (let i = 0; i < deltas.length; i++) {
      const before = [...spoken];
      const r = assemble({ deltas: deltas.slice(0, i + 1), emitted });
      emitted = r.emitted;
      spoken.push(...r.sentences);
      expect(spoken.slice(0, before.length)).toEqual(before);
    }

    // `<` より前は確定しているので発話済み。`< … >` の中身は削除されて残骸になる
    expect(spoken).toEqual(["まず確認します。", "条件は a  d です。"]);
  });

  it("emitted が実際の文数より進んでいても、以降が無言にならない", () => {
    // 縮みが起きた直後を模した状態。ここで固定してしまうと二度と喋らなくなる
    const shrunk = assemble({ deltas: ["あ。い。"], emitted: 5 });
    expect(shrunk.sentences).toEqual([]);
    expect(shrunk.emitted).toBe(2);

    const grown = assemble({ deltas: ["あ。い。う。え。"], emitted: shrunk.emitted, final: true, flushPending: true });
    expect(grown.sentences).toEqual(["う。", "え。"]);
  });
});

describe("後続イベントによる保留解除", () => {
  it("文として閉じていれば最後の文も出す", () => {
    const r = assemble({ deltas: ["確認します。ログを見ます。"], emitted: 1, flushPending: true });
    expect(r.sentences).toEqual(["ログを見ます。"]);
  });

  it("★ 文の途中なら出さない（断片を読み上げない）", () => {
    const r = assemble({ deltas: ["Aの一文目。Aの途中"], emitted: 1, flushPending: true });
    expect(r.sentences).toEqual([]);
    expect(r.emitted).toBe(1);
  });

  it("行が変わっていれば閉じているとみなす", () => {
    const r = assemble({ deltas: ["見出し\n本文です\n"], emitted: 1, flushPending: true });
    expect(r.sentences).toEqual(["本文です"]);
  });

  it("final なら文の途中でも出す（もう続きは来ない）", () => {
    const r = assemble({ deltas: ["Aの一文目。Aの途中"], emitted: 1, final: true, flushPending: true });
    expect(r.sentences).toEqual(["Aの途中"]);
  });

  it("先に流した文を、遅れて届いた final で二度出さない", () => {
    const deltas = ["名前と構成、両方の回答に1点だけ噛み合わない所があります。設計をまとめます。"];

    const flushed = assemble({ deltas, emitted: 1, flushPending: true });
    expect(flushed.sentences).toEqual(["設計をまとめます。"]);

    const onFinal = assemble({ deltas, emitted: flushed.emitted, final: true, flushPending: true });
    expect(onFinal.sentences).toEqual([]);
  });
});

describe("実測ログに近い流れ", () => {
  it("ツール呼び出し手前までの文が、final を待たずに順に出る", () => {
    // 設計書 §2-4 の 7e2d4582 に近い形（最終チャンクだけ大きく遅れる）
    const beforeFinal = [
      "完全に判明しました。ストリーミングでテキストが流れてきます。\n\n",
      "対照の `PostToolUse` も2件出たので、設定が読み込まれたことは確定です。\n\n",
      "3点わかりました。1つの `message_id` に対し index が振られます。",
    ];

    const early = stream(beforeFinal, false);
    expect(early.total).toEqual([
      "完全に判明しました。",
      "ストリーミングでテキストが流れてきます。",
      "対照の PostToolUse も2件出たので、設定が読み込まれたことは確定です。",
      "3点わかりました。",
    ]);

    // ずっと後になってようやく届く final チャンク
    const withFinal = assemble({
      deltas: [...beforeFinal, "これは実装方針に関わる分岐があるので確認させてください。"],
      emitted: early.total.length,
      final: true,
      flushPending: true,
    });

    expect(withFinal.sentences).toEqual([
      "1つの message_id に対し index が振られます。",
      "これは実装方針に関わる分岐があるので確認させてください。",
    ]);
  });
});

/**
 * revert 対象の回帰テスト（PR #15 レビュー #3）。
 *
 * 行が閉じていることは「もう変化しない」ことを意味しない。後続の delta が複数行構文
 * （引用+タグ、コードフェンス、表）を閉じると、`cleanTextForSpeech` が既に発話済みの
 * 範囲まで削除・変形する。`truncateAtUnstableTail` の切り詰め位置は別ルールが決めるため、
 * `safe` の中に未閉じ構文が残ったまま `safe` が `\n` で終わりうる。
 *
 * ここで守っているのは2つ:
 * - **未閉じ構文が混ざらない**（生のマークアップ・記号が読み上げられない）
 * - **同じ文が二度出ない**（一度出した範囲が後から変わらない。`emitted` が文数で
 *   進捗を持てる根拠がこれ）
 *
 * 行境界はこのどちらの根拠にもならない。`endsAtLineBoundary` はこれを見誤っていた。
 */
describe("行境界だけでは保留を外せない（endsAtLineBoundary の revert）", () => {
  it("引用のあとの未閉じタグは、行が変わっても保留する", () => {
    const { total } = stream(["> 引用です。\n", "<div\n", "`未閉じ\n", "</div>\n", "<div>\n"]);
    expect(total).toEqual(["引用です。"]);
  });

  it("バッククォート1つの行から始まっても、未閉じインラインコードとして保留する", () => {
    const { total } = stream(["`\n", "```文C。\n", "abc1234\n", "文A。```\n", "|a|b|\n"]);
    expect(total).toEqual([]);
  });

  it("コードフェンスの直後の行は、閉じるまで手前の文も含めて保留する", () => {
    const { total } = stream(["説明します。\n", "```ts\n", "const secret = 1;\n"]);
    expect(total).toEqual([]);
  });

  it("表の行が閉じていなければ、直前の文だけを保留する", () => {
    const { total } = stream(["手順です。\n", "| A | B |\n", "| C | D\n"]);
    expect(total).toEqual(["手順です。"]);
  });
});

describe("防御", () => {
  it("見出し・リストマーカー・URL は除去済みの文になる", () => {
    const r = assemble({
      deltas: ["## 見出し\n- 箇条書きです。詳細は https://example.com を見てください。"],
      final: true,
      flushPending: true,
    });
    expect(r.sentences).toEqual(["見出し", "箇条書きです。", "詳細は  を見てください。"]);
  });
});

/**
 * 移植した `cleanTextForSpeech`（上流 cc-mascot の10段の正規表現）が**扱っていない**記法。
 * 上流にもこれを保持する意図のテストは無く、単なる未対応。
 *
 * 実機で頻度を見てから整形規則をまとめて見直す方針にしたので、ここでは
 * **現状の挙動を固定して欠落を可視化するだけ**にしてある。
 * 詳細と直し方は docs/core.md の「既知の欠落」を参照。
 */
describe("既知の欠落（移植した cleanTextForSpeech の未対応記法）", () => {
  const speak = (text: string) => assemble({ deltas: [text], final: true, flushPending: true }).sentences;

  it("強調の ** が残る", () => {
    expect(speak("**最重要** の点を確認します。")).toEqual(["**最重要** の点を確認します。"]);
  });

  it("句点をまたぐ強調は、閉じの ** だけが1発話になってしまう", () => {
    expect(speak("**確認します。**")).toEqual(["**確認します。", "**"]);
  });

  it("斜体・下線強調・取り消し線も残る", () => {
    expect(speak("*斜体* と __太字__ と ~~取り消し~~ です。")).toEqual(["*斜体* と __太字__ と ~~取り消し~~ です。"]);
  });

  it("リンクは URL だけ消えて壊れた残骸になる", () => {
    // 段8（URL除去）がリンク記法を知らないため。直すなら段8より前に処理が要る
    expect(speak("詳細は [ドキュメント](https://example.com) を参照。")).toEqual(["詳細は [ドキュメント]( を参照。"]);
  });

  it("★ URL 直後に句点を書くと、その先が段落ごと消える", () => {
    expect(speak("参考は https://example.com。まず設計書を読みます。次に実装します。")).toEqual(["参考は"]);
  });

  it("★ commit hash の正規表現が数値や英単語まで消す", () => {
    expect(speak("ファイルは 5242880 バイトです。")).toEqual(["ファイルは  バイトです。"]);
    expect(speak("The word defaced was removed.")).toEqual(["The word  was removed."]);
  });

  it("約物だけの発話が seq を消費する", () => {
    expect(speak("すごい！！")).toEqual(["すごい！", "！"]);
  });
});
