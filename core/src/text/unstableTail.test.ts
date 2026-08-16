import { describe, it, expect } from "vitest";
import { truncateAtUnstableTail } from "./unstableTail";
import { cleanTextForSpeech, splitIntoSentences } from "./textFilter";

const cut = (text: string) => truncateAtUnstableTail(text);

describe("コードフェンス", () => {
  it("フェンスが無ければそのまま返す", () => {
    const text = "確認します。ログを見ますね。";
    expect(cut(text)).toBe(text);
  });

  it("空文字はそのまま返す", () => {
    expect(cut("")).toBe("");
  });

  it("閉じたフェンスはそのまま返す（除去は cleanTextForSpeech の仕事）", () => {
    const text = "こう書きます。\n```ts\nconst a = 1;\n```\n以上です。";
    expect(cut(text)).toBe(text);
  });

  it("未閉じのフェンスは開始位置より後ろを切り落とす（コードは読み上げない）", () => {
    expect(cut("こう書きます。\n```ts\nconst secret = 1;")).toBe("こう書きます。\n");
  });

  it("言語指定だけ届いた時点でも切り落とす", () => {
    expect(cut("こう書きます。\n```typescript")).toBe("こう書きます。\n");
  });

  it("フェンスが先頭にあれば空文字になる", () => {
    expect(cut("```\nconst a = 1;")).toBe("");
  });

  it("閉じたフェンスが複数あっても、最後の未閉じだけを切る", () => {
    const text = "1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n3つ目。\n```\nc";
    expect(cut(text)).toBe("1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n3つ目。\n");
  });

  it("バッククォート4つは3つ+1つとして数える（正規表現と同じ数え方）", () => {
    expect(cut("説明。\n````")).toBe("説明。\n");
    const closedFence = "説明。\n``````";
    expect(cut(closedFence)).toBe(closedFence);
  });
});

describe("書きかけの表の行", () => {
  it("閉じていない行は切る（生の行を読み上げない）", () => {
    expect(cut("手順です。\n| A | B |\n| C | D")).toBe("手順です。\n| A | B |\n");
  });

  it("行が閉じていればそのまま（除去は cleanTextForSpeech の仕事）", () => {
    const text = "手順です。\n| A | B |\n| C | D |\n完了です。";
    expect(cut(text)).toBe(text);
  });

  it("表でない行には反応しない", () => {
    const text = "手順です。\n次に進みます";
    expect(cut(text)).toBe(text);
  });

  it("★ 閉じたコードブロックの中の `|` 行には反応しない（scan の前処理）", () => {
    const text = "説明。\n```\n| a | b\n```\nこれで完了です。";
    expect(cut(text)).toBe(text);
  });
});

describe("保留しなくなったもの（final を待つので伸びない）", () => {
  it("未閉じの `<` は切らない", () => {
    const text = "条件は a < b です。まず確認します。";
    expect(cut(text)).toBe(text);
  });

  it("未閉じのインラインバッククォートは切らない", () => {
    const text = "実行は `npm run build";
    expect(cut(text)).toBe(text);
  });

  it("末尾の URL は切らない", () => {
    const text = "参考は https://example.com/pa";
    expect(cut(text)).toBe(text);
  });

  it("末尾の16進列は切らない", () => {
    const text = "コミットは abc123";
    expect(cut(text)).toBe(text);
  });
});

describe("cleanTextForSpeech との組み合わせ（この関数が存在する理由）", () => {
  const speak = (raw: string) => splitIntoSentences(cleanTextForSpeech(truncateAtUnstableTail(raw))).filter(Boolean);

  it("未閉じのままではコードが読み上げに漏れる", () => {
    expect(cleanTextForSpeech("こう書きます。\n```ts\nconst secret = 1;")).toContain("const secret = 1;");
  });

  it("切り落としてから整形すればコードは漏れない", () => {
    expect(speak("こう書きます。\n```ts\nconst secret = 1;")).toEqual(["こう書きます。"]);
  });

  it("未閉じのままでは表の行が生で読み上げに漏れる", () => {
    expect(cleanTextForSpeech("手順です。\n| C | D")).toContain("| C | D");
  });

  it("切り落としてから整形すれば表の行は漏れない", () => {
    expect(speak("手順です。\n| C | D")).toEqual(["手順です。"]);
  });
});
