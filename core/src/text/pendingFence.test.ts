import { describe, it, expect } from "vitest";
import { truncateAtUnclosedFence } from "./pendingFence";
import { cleanTextForSpeech } from "./textFilter";

describe("truncateAtUnclosedFence", () => {
  it("フェンスが無ければそのまま返す", () => {
    const text = "確認します。ログを見ますね。";
    expect(truncateAtUnclosedFence(text)).toBe(text);
  });

  it("空文字はそのまま返す", () => {
    expect(truncateAtUnclosedFence("")).toBe("");
  });

  it("閉じたフェンスはそのまま返す（除去は cleanTextForSpeech の仕事）", () => {
    const text = "こう書きます。\n```ts\nconst a = 1;\n```\n以上です。";
    expect(truncateAtUnclosedFence(text)).toBe(text);
  });

  it("未閉じのフェンスは開始位置より後ろを切り落とす", () => {
    const text = "こう書きます。\n```ts\nconst a = 1;";
    expect(truncateAtUnclosedFence(text)).toBe("こう書きます。\n");
  });

  it("言語指定だけ届いた時点でも切り落とす", () => {
    expect(truncateAtUnclosedFence("こう書きます。\n```typescript")).toBe("こう書きます。\n");
  });

  it("フェンスが先頭にあれば空文字になる", () => {
    expect(truncateAtUnclosedFence("```\nconst a = 1;")).toBe("");
  });

  it("閉じたフェンスが複数あっても、最後の未閉じだけを切る", () => {
    const text = "1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n3つ目。\n```\nc";
    expect(truncateAtUnclosedFence(text)).toBe("1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n3つ目。\n");
  });

  it("すべて閉じていれば末尾まで残す", () => {
    const text = "1つ目。\n```\na\n```\n2つ目。\n```\nb\n```\n終わり。";
    expect(truncateAtUnclosedFence(text)).toBe(text);
  });

  it("インラインコードのバッククォートには反応しない", () => {
    const text = "`const a = 1` と書きます。";
    expect(truncateAtUnclosedFence(text)).toBe(text);
  });

  it("バッククォート4つは3つ+1つとして数える（正規表現と同じ数え方）", () => {
    // "````" は先頭3つが開きで、残る1つは FENCE に満たないので開いたまま
    expect(truncateAtUnclosedFence("説明。\n````")).toBe("説明。\n");
    // "``````" は 3+3 で閉じる
    const closed = "説明。\n``````";
    expect(truncateAtUnclosedFence(closed)).toBe(closed);
  });

  describe("cleanTextForSpeech との組み合わせ（この関数が存在する理由）", () => {
    it("未閉じのままではコードが読み上げに漏れる", () => {
      const raw = "こう書きます。\n```ts\nconst secret = 1;";
      expect(cleanTextForSpeech(raw)).toContain("const secret = 1;");
    });

    it("切り落としてから整形すればコードは漏れない", () => {
      const raw = "こう書きます。\n```ts\nconst secret = 1;";
      const cleaned = cleanTextForSpeech(truncateAtUnclosedFence(raw));
      expect(cleaned).not.toContain("const secret");
      expect(cleaned).toContain("こう書きます。");
    });

    it("フェンスが閉じても、手前の確定済みテキストは変化しない", () => {
      const opening = "こう書きます。\n```ts\nconst a = 1;";
      const closed = `${opening}\n\`\`\`\n以上です。`;

      const before = cleanTextForSpeech(truncateAtUnclosedFence(opening));
      const after = cleanTextForSpeech(truncateAtUnclosedFence(closed));

      expect(after.startsWith(before)).toBe(true);
    });
  });
});
