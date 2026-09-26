import { describe, it, expect } from "vitest";
import { pickEmotion } from "./emotionScores";

describe("pickEmotion", () => {
  it("最大値のラベルを返す", () => {
    expect(pickEmotion({ happy: 0.9, relaxed: 0.1, surprised: 0.1, sad: 0.1, angry: 0.1, neutral: 0.1 })).toBe("happy");
    expect(pickEmotion({ happy: 0.1, relaxed: 0.1, surprised: 0.1, sad: 0.9, angry: 0.1, neutral: 0.1 })).toBe("sad");
  });

  /** ★ 先頭キー（happy）に偏らない。全部0点は「何も無い」であって「達成」ではない */
  it("★ 全部0点なら neutral", () => {
    expect(pickEmotion({ happy: 0, relaxed: 0, surprised: 0, sad: 0, angry: 0, neutral: 0 })).toBe("neutral");
  });

  /** ★ happy と sad が同値でも happy に倒さない（謝罪・失敗報告の文が笑顔になる事故を避ける） */
  it("★ 同点なら neutral", () => {
    expect(pickEmotion({ happy: 0.7, relaxed: 0.1, surprised: 0.1, sad: 0.7, angry: 0.1, neutral: 0.1 })).toBe(
      "neutral",
    );
  });

  it("★ neutral 自身が単独最大なら neutral をそのまま返す", () => {
    expect(pickEmotion({ happy: 0.1, relaxed: 0.1, surprised: 0.1, sad: 0.1, angry: 0.1, neutral: 0.9 })).toBe(
      "neutral",
    );
  });

  it("壊れた入力は null（null / 非オブジェクト / 有効なキーが1つも無い）", () => {
    expect(pickEmotion(null)).toBeNull();
    expect(pickEmotion(undefined)).toBeNull();
    expect(pickEmotion({})).toBeNull();
    expect(pickEmotion({ happy: "x" as unknown as number })).toBeNull();
  });

  it("一部のキーが欠けていても、残りの有効な値から選ぶ", () => {
    expect(pickEmotion({ happy: 0.8 })).toBe("happy");
  });
});
