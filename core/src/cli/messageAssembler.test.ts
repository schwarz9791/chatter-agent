import { describe, it, expect } from "vitest";
import { assembleSentences } from "./messageAssembler";

/** delta が1つずつ届く流れを再現し、各ステップで出力された文を集める */
function stream(deltas: string[], finalAtEnd = false): { steps: string[][]; total: string[] } {
  const steps: string[][] = [];
  const total: string[] = [];
  let emitted = 0;

  for (let i = 0; i < deltas.length; i++) {
    const isLast = i === deltas.length - 1;
    const result = assembleSentences({
      deltas: deltas.slice(0, i + 1),
      emitted,
      flushPending: finalAtEnd && isLast,
    });
    emitted = result.emitted;
    steps.push(result.sentences);
    total.push(...result.sentences);
  }
  return { steps, total };
}

describe("assembleSentences", () => {
  it("最後の文は保留する（まだ伸びうるため）", () => {
    const r = assembleSentences({ deltas: ["確認します。ログを見ます。"], emitted: 0, flushPending: false });
    expect(r.sentences).toEqual(["確認します。"]);
    expect(r.emitted).toBe(1);
  });

  it("flushPending で保留していた最後の文も出す", () => {
    const r = assembleSentences({ deltas: ["確認します。ログを見ます。"], emitted: 1, flushPending: true });
    expect(r.sentences).toEqual(["ログを見ます。"]);
    expect(r.emitted).toBe(2);
  });

  it("文が1つしか無い間は何も出さない", () => {
    const r = assembleSentences({ deltas: ["確認します。"], emitted: 0, flushPending: false });
    expect(r.sentences).toEqual([]);
    expect(r.emitted).toBe(0);
  });

  it("空の delta では何も出さない", () => {
    expect(assembleSentences({ deltas: [], emitted: 0, flushPending: false }).sentences).toEqual([]);
    expect(assembleSentences({ deltas: [""], emitted: 0, flushPending: true }).sentences).toEqual([]);
  });

  it("同じ文を二度出さない", () => {
    const first = assembleSentences({ deltas: ["あ。い。"], emitted: 0, flushPending: false });
    expect(first.sentences).toEqual(["あ。"]);

    const second = assembleSentences({ deltas: ["あ。い。う。"], emitted: first.emitted, flushPending: false });
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

describe("未閉じコードブロックの保留（CLAUDE.md 絶対に守ること1）", () => {
  it("開いたままのコードは読み上げない", () => {
    const r = assembleSentences({
      deltas: ["こう書きます。\n```ts\nconst secret = 1;\n"],
      emitted: 0,
      flushPending: true,
    });
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
});

describe("実測ログに近い流れ", () => {
  it("ツール呼び出し手前までの文が、final を待たずに順に出る", () => {
    // 設計書 §2-4 の 7e2d4582 に近い形（最終チャンクだけ 60 秒遅れる）
    const beforeFinal = [
      "完全に判明しました。**ストリーミングでテキストが流れてきます。**\n\n",
      "対照の `PostToolUse` も2件出たので、設定が読み込まれたことは確定です。\n\n",
      "3点わかりました。1つの `message_id` に対し index が振られます。",
    ];

    const early = stream(beforeFinal, false);
    expect(early.total).toEqual([
      "完全に判明しました。",
      // ↓ 強調の ** が残るのは移植した cleanTextForSpeech の既知の欠落。
      //   下の「既知の欠落」ブロックを参照。ここでは現状の挙動をそのまま固定する
      "**ストリーミングでテキストが流れてきます。",
      "**",
      "対照の PostToolUse も2件出たので、設定が読み込まれたことは確定です。",
      "3点わかりました。",
    ]);

    // 60 秒後にようやく届く final チャンク
    let emitted = early.total.length;
    const withFinal = assembleSentences({
      deltas: [...beforeFinal, "これは実装方針に関わる分岐があるので確認させてください。"],
      emitted,
      flushPending: true,
    });
    emitted = withFinal.emitted;

    expect(withFinal.sentences).toEqual([
      "1つの message_id に対し index が振られます。",
      "これは実装方針に関わる分岐があるので確認させてください。",
    ]);
  });

  it("後続イベントで先に流した最後の文を、final 到着時に二度出さない", () => {
    const deltas = ["名前と構成、両方の回答に1点だけ噛み合わない所があります。設計をまとめます。"];

    // 別のイベントが届いたので保留を解いて全部流す
    const flushed = assembleSentences({ deltas, emitted: 1, flushPending: true });
    expect(flushed.sentences).toEqual(["設計をまとめます。"]);

    // 80 秒後に final:true が届いても、新しい文が無ければ何も出ない
    const onFinal = assembleSentences({ deltas, emitted: flushed.emitted, flushPending: true });
    expect(onFinal.sentences).toEqual([]);
  });
});

describe("防御", () => {
  it("emitted が実際の文数より進んでいても壊れない（後退させない）", () => {
    const r = assembleSentences({ deltas: ["あ。い。"], emitted: 5, flushPending: true });
    expect(r.sentences).toEqual([]);
    expect(r.emitted).toBe(5);
  });

  it("見出し・リストマーカー・URL は除去済みの文になる", () => {
    const r = assembleSentences({
      deltas: ["## 見出し\n- 箇条書きです。詳細は https://example.com を見てください。"],
      emitted: 0,
      flushPending: true,
    });
    expect(r.sentences).toEqual(["見出し", "箇条書きです。", "詳細は  を見てください。"]);
  });
});

/**
 * 移植した `cleanTextForSpeech`（上流 cc-mascot の10段の正規表現）が**扱っていない**記法。
 * 上流にもこれを保持する意図のテストは無く、単なる未対応。
 *
 * TTS にそのまま渡ると読み上げに混ざる（`**` だけの行が1発話になることもある）が、
 * 実機で頻度を見てから整形規則をまとめて見直す方針にしたので、
 * ここでは**現状の挙動を固定して欠落を可視化するだけ**にしてある。
 * 直すときはこのブロックの期待値を書き換えることになる。
 */
describe("既知の欠落（移植した cleanTextForSpeech の未対応記法）", () => {
  function speak(text: string): string[] {
    return assembleSentences({ deltas: [text], emitted: 0, flushPending: true }).sentences;
  }

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
});
