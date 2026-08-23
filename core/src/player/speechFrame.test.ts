import { describe, it, expect } from "vitest";
import { parseSpeechFrame } from "./speechFrame";

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
      audio: null,
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

describe("audio（#29）", () => {
  it("音声の参照を読む", () => {
    const audio = { path: "/audio/test-epoch-000000000001.wav", format: "wav" };
    expect(parseSpeechFrame(frame({ audio }))?.audio).toEqual(audio);
  });

  it("★ 絶対 URL を通さない（サーバーがクライアントを任意の外部ホストへ向かわせられる）", () => {
    for (const path of [
      "http://evil.example.com/audio/test-epoch-000000000001.wav",
      "//evil.example.com/audio/test-epoch-000000000001.wav",
      "/audio/../../etc/passwd",
      "/etc/passwd",
    ]) {
      expect(parseSpeechFrame(frame({ audio: { path, format: "wav" } }))?.audio, path).toBeNull();
    }
  });

  it("読めない audio は null に倒す（フレームごと捨てない）", () => {
    for (const audio of [null, "wav", 1, {}, { path: "/audio/test-epoch-000000000001.wav" }, { format: "wav" }]) {
      expect(parseSpeechFrame(frame({ audio }))?.audio, JSON.stringify(audio)).toBeNull();
    }
    // フレームそのものは読めている
    expect(parseSpeechFrame(frame({ audio: null }))?.seq).toBe(1);
  });

  it("知らない format は通さない", () => {
    const audio = { path: "/audio/test-epoch-000000000001.wav", format: "opus" };
    expect(parseSpeechFrame(frame({ audio }))?.audio).toBeNull();
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
