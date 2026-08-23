import { describe, it, expect } from "vitest";
import { hasSpeakableText } from "./speakable";

describe("hasSpeakableText", () => {
  it("文字があれば true", () => {
    expect(hasSpeakableText("こんにちは。")).toBe(true);
    expect(hasSpeakableText("OK")).toBe(true);
    expect(hasSpeakableText("2026年")).toBe(true);
  });

  it("約物・空白だけなら false（文分割が作る「！」だけの断片）", () => {
    // docs/core.md「既知の欠落」: すごい！！ → ["すごい！", "！"]
    expect(hasSpeakableText("！")).toBe(false);
    expect(hasSpeakableText("。。。")).toBe(false);
    expect(hasSpeakableText("   ")).toBe(false);
    expect(hasSpeakableText("")).toBe(false);
    expect(hasSpeakableText("…")).toBe(false);
    expect(hasSpeakableText("、")).toBe(false);
  });

  it("読まれうる記号（単位・通貨）は true", () => {
    expect(hasSpeakableText("℃")).toBe(true);
    expect(hasSpeakableText("%")).toBe(true);
    expect(hasSpeakableText("¥")).toBe(true);
    expect(hasSpeakableText("$")).toBe(true);
  });

  it("★ コードの断片だけの行は false（\\p{S} を丸ごと通すと届いてしまう）", () => {
    // 文分割は `=>` や `^^` だけの断片を作ることがある。/audio_query は
    // 空の WAV か 4xx を返すので、合成に出す前にここで落とす
    expect(hasSpeakableText("=>")).toBe(false);
    expect(hasSpeakableText("^^")).toBe(false);
    expect(hasSpeakableText("```")).toBe(false);
    expect(hasSpeakableText("+")).toBe(false);
    expect(hasSpeakableText("~")).toBe(false);
    expect(hasSpeakableText("<=")).toBe(false);
  });
});
