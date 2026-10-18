import { describe, it, expect } from "vitest";
import { engineKeys, voiceLanguage } from "./engineKeys";

describe("engineKeys", () => {
  it("openai は Kokoro のキーを返す", () => {
    expect(engineKeys("openai")).toEqual({ baseUrl: "kokoroBaseUrl", voice: "kokoroVoiceId" });
  });

  it("それ以外は AivisSpeech のキーを返す", () => {
    expect(engineKeys("voicevox")).toEqual({ baseUrl: "ttsBaseUrl", voice: "ttsSpeakerId" });
  });
});

describe("voiceLanguage", () => {
  it("openai 以外は日本語", () => {
    expect(voiceLanguage("voicevox", "af_heart")).toBe("ja");
  });

  it.each([
    ["af_heart", "en"],
    ["bf_emma", "en"],
    ["jf_alpha", "ja"],
    ["JF_alpha", "ja"],
    ["af_bella+jf_alpha", "en"],
    ["jf_alpha+af_bella", "ja"],
    ["zf_xiaobei", "en"],
  ])("openai で声が %s なら %s", (voice, expected) => {
    expect(voiceLanguage("openai", voice)).toBe(expected);
  });
});
