import { describe, it, expect } from "vitest";
import { buildAudioPath, isAudioPath, parseAudioPath } from "./audioPath";

describe("buildAudioPath", () => {
  it("epoch と 12桁ゼロ埋めした seq でパスを組む", () => {
    expect(buildAudioPath("gen-1", 42)).toBe("/audio/gen-1-000000000042.wav");
  });

  it("組んだものは必ず読み戻せる", () => {
    for (const [epoch, seq] of [
      ["gen-1", 1],
      ["1f0a9c3e-5b62-4f1d-9a77-0e2c8d4b6a31", 999],
      ["legacy", 123456789012],
    ] as const) {
      expect(parseAudioPath(buildAudioPath(epoch, seq))).toEqual({ epoch, seq });
    }
  });
});

describe("parseAudioPath", () => {
  it("epoch に含まれる - に引きずられない（seq は固定幅なので後ろから決まる）", () => {
    expect(parseAudioPath("/audio/gen-1-2-3-000000000007.wav")).toEqual({ epoch: "gen-1-2-3", seq: 7 });
  });

  it("★ 知らない形は通さない（受け取った文字列をパスの組み立てに使わないための最初の関門）", () => {
    for (const raw of [
      "",
      "/",
      "/audio/",
      "/audio/gen-1.wav", // seq が無い
      "/audio/gen-1-42.wav", // 桁数が足りない
      "/audio/gen-1-000000000042.mp3",
      "/audio/gen-1-000000000042.wav/", // 末尾の余り
      "/audio/../../etc/passwd",
      "/audio/..-000000000001.wav", // epoch の先頭は英数字だけ
      "/audio/%2e%2e-000000000001.wav",
      "/AUDIO/gen-1-000000000042.wav",
      "/audio/gen 1-000000000042.wav",
      `/audio/${"a".repeat(65)}-000000000042.wav`,
      "/audio/gen-1-000000000000.wav", // seq は 1 始まり
    ]) {
      expect(parseAudioPath(raw), raw).toBeNull();
    }
  });
});

describe("isAudioPath", () => {
  it("★ 絶対 URL を通さない（サーバーがクライアントを任意の外部ホストへ向かわせられる）", () => {
    expect(isAudioPath("/audio/gen-1-000000000001.wav")).toBe(true);
    for (const raw of [
      "http://evil.example.com/audio/gen-1-000000000001.wav",
      "//evil.example.com/audio/gen-1-000000000001.wav",
      "file:///etc/passwd",
      42,
      null,
      undefined,
    ]) {
      expect(isAudioPath(raw), String(raw)).toBe(false);
    }
  });
});
