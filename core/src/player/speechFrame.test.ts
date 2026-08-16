import { describe, it, expect } from "vitest";
import { hasSpeakableText, parseSpeechFrame } from "./speechFrame";

function frame(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    seq: 1,
    ts: "2026-08-15T00:00:00.000Z",
    source: "claude-code",
    sessionId: "sess-1",
    turnId: "turn-1",
    messageId: "m1",
    kind: "assistant",
    text: "こんにちは。",
    emotion: "happy",
    ...overrides,
  });
}

describe("parseSpeechFrame", () => {
  it("正常なフレームをそのまま読む", () => {
    expect(parseSpeechFrame(frame())).toEqual({
      seq: 1,
      ts: "2026-08-15T00:00:00.000Z",
      source: "claude-code",
      sessionId: "sess-1",
      turnId: "turn-1",
      messageId: "m1",
      kind: "assistant",
      text: "こんにちは。",
      emotion: "happy",
    });
  });

  it("JSON でないものは null", () => {
    expect(parseSpeechFrame("not json")).toBeNull();
    expect(parseSpeechFrame("")).toBeNull();
  });

  it("オブジェクトでないものは null", () => {
    expect(parseSpeechFrame("[1,2,3]")).toBeNull();
    expect(parseSpeechFrame("null")).toBeNull();
    expect(parseSpeechFrame('"text"')).toBeNull();
  });

  it("★ seq は非負の安全整数だけを通す（Map のキー・ack の値・一時ファイル名の材料）", () => {
    expect(parseSpeechFrame(frame({ seq: -1 }))).toBeNull();
    expect(parseSpeechFrame(frame({ seq: 1.5 }))).toBeNull();
    expect(parseSpeechFrame(frame({ seq: "1" }))).toBeNull();
    expect(parseSpeechFrame(frame({ seq: Number.MAX_SAFE_INTEGER + 1 }))).toBeNull();
    expect(parseSpeechFrame(frame({ seq: undefined }))).toBeNull();
  });

  it("text が文字列でなければ null", () => {
    expect(parseSpeechFrame(frame({ text: 42 }))).toBeNull();
    expect(parseSpeechFrame(frame({ text: undefined }))).toBeNull();
  });

  it("★ ts は必須（重複排除のキーに使うので seq 単独では足りない）", () => {
    expect(parseSpeechFrame(frame({ ts: undefined }))).toBeNull();
    expect(parseSpeechFrame(frame({ ts: "" }))).toBeNull();
    expect(parseSpeechFrame(frame({ ts: 1755216754000 }))).toBeNull();
  });

  it("空文字の text は通す（合成に出すかは hasSpeakableText で決める）", () => {
    expect(parseSpeechFrame(frame({ text: "" }))?.text).toBe("");
  });

  it("未知の kind は assistant として扱う（protocol.md の要求）", () => {
    expect(parseSpeechFrame(frame({ kind: "system" }))?.kind).toBe("assistant");
    expect(parseSpeechFrame(frame({ kind: undefined }))?.kind).toBe("assistant");
    expect(parseSpeechFrame(frame({ kind: 1 }))?.kind).toBe("assistant");
    // 既知の kind は保つ
    expect(parseSpeechFrame(frame({ kind: "prompt" }))?.kind).toBe("prompt");
  });

  it("未知の emotion は neutral に丸める", () => {
    expect(parseSpeechFrame(frame({ emotion: "excited" }))?.emotion).toBe("neutral");
    expect(parseSpeechFrame(frame({ emotion: undefined }))?.emotion).toBe("neutral");
  });

  it("識別子が欠けていても null で埋めて読む", () => {
    const r = parseSpeechFrame(frame({ sessionId: undefined, turnId: null, messageId: 42 }));
    expect(r?.sessionId).toBeNull();
    expect(r?.turnId).toBeNull();
    expect(r?.messageId).toBeNull();
  });
});

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

  it("記号でも読まれうるものは true", () => {
    expect(hasSpeakableText("℃")).toBe(true);
    expect(hasSpeakableText("+")).toBe(true);
  });
});
