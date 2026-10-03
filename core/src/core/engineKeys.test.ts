import { describe, it, expect } from "vitest";
import { engineKeys } from "./engineKeys";

describe("engineKeys", () => {
  it("openai は Kokoro のキーを返す", () => {
    expect(engineKeys("openai")).toEqual({ baseUrl: "kokoroBaseUrl", voice: "kokoroVoiceId" });
  });

  it("それ以外は AivisSpeech のキーを返す", () => {
    expect(engineKeys("voicevox")).toEqual({ baseUrl: "ttsBaseUrl", voice: "ttsSpeakerId" });
  });
});
