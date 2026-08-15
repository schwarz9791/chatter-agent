import { describe, it, expect } from "vitest";
import { truncateAtUnstableTail } from "./unstableTail";
import { cleanTextForSpeech, splitIntoSentences } from "./textFilter";

/** ストリーミング中（final はまだ来ていない） */
const streaming = (text: string) => truncateAtUnstableTail(text);
/** final:true を処理した後 */
const closed = (text: string) => truncateAtUnstableTail(text, { final: true });

describe("コードフェンス", () => {
  it("フェンスが無ければそのまま返す", () => {
    const text = "確認します。ログを見ますね。";
    expect(streaming(text)).toBe(text);
  });

  it("空文字はそのまま返す", () => {
    expect(streaming("")).toBe("");
  });

  it("閉じたフェンスはそのまま返す（除去は cleanTextForSpeech の仕事）", () => {
    const text = "こう書きます。\n```ts\nconst a = 1;\n```\n以上です。";
    expect(streaming(text)).toBe(text);
  });

  it("未閉じのフェンスは開始位置より後ろを切り落とす", () => {
    expect(streaming("こう書きます。\n```ts\nconst a = 1;")).toBe("こう書きます。\n");
  });

  it("言語指定だけ届いた時点でも切り落とす", () => {
    expect(streaming("こう書きます。\n```typescript")).toBe("こう書きます。\n");
  });

  it("フェンスが先頭にあれば空文字になる", () => {
    expect(streaming("```\nconst a = 1;")).toBe("");
  });

  it("閉じたフェンスが複数あっても、最後の未閉じだけを切る", () => {
    const text = "1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n3つ目。\n```\nc";
    expect(streaming(text)).toBe("1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n3つ目。\n");
  });

  it("バッククォート4つは3つ+1つとして数える（正規表現と同じ数え方）", () => {
    expect(streaming("説明。\n````")).toBe("説明。\n");
    const closedFence = "説明。\n``````";
    expect(streaming(closedFence)).toBe(closedFence);
  });

  it("final でも未閉じフェンスは切る（コードは読み上げない）", () => {
    expect(closed("こう書きます。\n```ts\nconst secret = 1;")).toBe("こう書きます。\n");
  });
});

describe("未閉じの `<`（★ 既出の文を巻き込んで消す）", () => {
  it("`>` が来るまで `<` 以降を保留する", () => {
    expect(streaming("条件は a < b です。まず確認します。")).toBe("条件は a ");
  });

  it("`>` が来たら保留を解く", () => {
    const text = "条件は a < b です。まず確認します。順序は c > d です。";
    expect(streaming(text)).toBe(text);
  });

  it("閉じたコードブロックの中の `<` は数えない", () => {
    const text = "説明。\n```\na < b\n```\nこれで完了です。";
    expect(streaming(text)).toBe(text);
  });

  it("final なら保留しない（もう伸びないので `<…>` は成立しない）", () => {
    const text = "条件は a < b です。まず確認します。";
    expect(closed(text)).toBe(text);
  });
});

describe("未閉じのインラインバッククォート", () => {
  it("閉じるまで保留する", () => {
    expect(streaming("実行は `npm run build")).toBe("実行は ");
  });

  it("閉じていればそのまま", () => {
    const text = "実行は `npm run build` です。";
    expect(streaming(text)).toBe(text);
  });

  it("final なら保留しない", () => {
    const text = "実行は `npm run build";
    expect(closed(text)).toBe(text);
  });
});

describe("書きかけの表の行", () => {
  it("行が閉じるまで保留する（生の行を読み上げない）", () => {
    expect(streaming("手順です。\n| A | B |\n| C | D")).toBe("手順です。\n| A | B |\n");
  });

  it("行が閉じていればそのまま（除去は cleanTextForSpeech の仕事）", () => {
    const text = "手順です。\n| A | B |\n| C | D |\n完了です。";
    expect(streaming(text)).toBe(text);
  });

  it("final でも書きかけの行は切る（生の表を読み上げない）", () => {
    expect(closed("手順です。\n| C | D")).toBe("手順です。\n");
  });

  it("表でない行には反応しない", () => {
    const text = "手順です。\n次に進みます";
    expect(streaming(text)).toBe(text);
  });
});

describe("末尾の URL と16進列", () => {
  it("末尾の URL は空白が来るまで保留する（削除範囲が伸び続けるため）", () => {
    expect(streaming("参考は https://example.com/pa")).toBe("参考は ");
  });

  it("空白が来れば保留を解く", () => {
    const text = "参考は https://example.com です。";
    expect(streaming(text)).toBe(text);
  });

  it("末尾の16進列は保留する（7文字目で消えるため）", () => {
    expect(streaming("コミットは abc123")).toBe("コミットは ");
  });

  it("final なら保留しない", () => {
    const text = "参考は https://example.com/pa";
    expect(closed(text)).toBe(text);
  });
});

describe("cleanTextForSpeech との組み合わせ（この関数が存在する理由）", () => {
  const speak = (raw: string) => splitIntoSentences(cleanTextForSpeech(truncateAtUnstableTail(raw))).filter(Boolean);
  /** ストリーミング中に実際に発話される範囲（最後の文は保留される） */
  const spoken = (raw: string) => speak(raw).slice(0, -1);

  it("未閉じのままではコードが読み上げに漏れる", () => {
    expect(cleanTextForSpeech("こう書きます。\n```ts\nconst secret = 1;")).toContain("const secret = 1;");
  });

  it("切り落としてから整形すればコードは漏れない", () => {
    expect(speak("こう書きます。\n```ts\nconst secret = 1;")).toEqual(["こう書きます。"]);
  });

  it("★ 発話済みの文が、後から届いた `>` で消えない", () => {
    const before = spoken("まず確認します。条件は a < b です。次にビルドします。");
    const after = spoken("まず確認します。条件は a < b です。次にビルドします。順序は c > d です。テストします。");

    expect(before).toEqual(["まず確認します。"]);
    expect(after.slice(0, before.length)).toEqual(before);
  });

  it("★ 発話済みの文が、後から閉じたフェンスで消えない", () => {
    const before = spoken("説明します。次のとおりです。\n```ts\nconst a = 1;");
    const after = spoken("説明します。次のとおりです。\n```ts\nconst a = 1;\n```\n以上です。");

    expect(before).toEqual(["説明します。"]);
    expect(after.slice(0, before.length)).toEqual(before);
  });

  it("★ 発話済みの文が、後から閉じた表の行で消えない", () => {
    const before = spoken("手順です。まとめました。\n| A | B |\n| C | D");
    const after = spoken("手順です。まとめました。\n| A | B |\n| C | D |\n完了です。");

    expect(before).toEqual(["手順です。"]);
    expect(after.slice(0, before.length)).toEqual(before);
  });
});
