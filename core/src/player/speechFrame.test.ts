import { describe, it, expect } from "vitest";
import { hasSpeakableText, parseSpeechFrame } from "./speechFrame";

function frame(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    epoch: "test-epoch",
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
      epoch: "test-epoch",
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

  it("★ seq は 1 始まりの安全整数だけを通す（Map のキー・ack の値・一時ファイル名の材料）", () => {
    // 0 は水位の初期値と衝突して余計な resetEpoch を起こし、`dispatcher.ack` が
    // 0 を no-op として扱うのでそのキューは永久に消えない
    expect(parseSpeechFrame(frame({ seq: 0 }))).toBeNull();
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

describe("epoch の検証（#29）", () => {
  it("★ 形が違う epoch は通さない（一時ファイル名と音声 URL の材料になる）", () => {
    for (const epoch of ["", 123, null, {}, "../../etc/passwd", "-leading-hyphen", "a".repeat(65)]) {
      expect(parseSpeechFrame(frame({ epoch })), JSON.stringify(epoch)).toBeNull();
    }
    // 欠落も通さない（サーバー側の speechQueue.read が legacy に正規化して送ってくる）
    expect(parseSpeechFrame(JSON.stringify({ seq: 1, ts: "t", text: "あ。" }))).toBeNull();
  });

  it("charset に収まる epoch は通す", () => {
    for (const epoch of ["legacy", "1f0a9c3e-5b62-4f1d-9a77-0e2c8d4b6a31", "a", "A.b_c-1", "a".repeat(64)]) {
      expect(parseSpeechFrame(frame({ epoch }))?.epoch, epoch).toBe(epoch);
    }
  });
});
