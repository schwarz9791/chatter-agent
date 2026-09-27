/**
 * OpenAI 互換の音声合成 API（`POST /v1/audio/speech` / `GET /v1/audio/voices`）を叩く
 * `TtsEngine`（→ `tts/ttsEngine.ts`）。第一の相手は Kokoro-FastAPI
 * （`server/engineProcess.ts` の `resolveKokoroSpawn` が起こす）。
 *
 * ★ `baseUrl` は **`/v1` を含まない origin**。`ttsSpawn` が `--host`/`--port` を
 *   `ttsBaseUrl` から導出するのと揃うように、パスはこのクライアントが足す。
 *
 * ★ **`speedScale` は毎回明示で送る。** VOICEVOX 系（`voicevoxClient.ts`）と違い
 *   「省略は触らない」という余地がここには無い —— `speed` は `POST /v1/audio/speech` の
 *   必須フィールドではないが、省略するとエンジン側の既定に委ねることになり、
 *   `ttsSpeedScale` を明示的に指定したのに効かない、という取り違えが起きる。
 */

import {
  createRequester,
  describe,
  looksLikeWav,
  TtsHttpError,
  type TtsEngine,
  type TtsEngineOptions,
} from "./ttsEngine";

/** 固定のモデル名。Kokoro-FastAPI は `"tts-1"` と `"kokoro"` のどちらも受ける */
const MODEL = "tts-1";

interface RawVoice {
  id?: unknown;
}

/** 声の一覧は `{id, name}` の配列でも、文字列だけの配列でも返ってくる。両方受ける */
function normalizeVoices(list: unknown[]): { id: string; label: string }[] {
  const out: { id: string; label: string }[] = [];
  for (const item of list) {
    if (typeof item === "string") {
      out.push({ id: item, label: item });
      continue;
    }
    const id = (item as RawVoice)?.id;
    if (typeof id === "string") out.push({ id, label: id });
  }
  return out;
}

export function createOpenAiEngine(options: TtsEngineOptions): TtsEngine {
  const { baseUrl, voiceId, timeoutMs, speedScale } = options;
  const request = createRequester(timeoutMs);

  return {
    baseUrl,

    async synthesize(text) {
      // ★ 存在しない声・空の入力は 400（404 ではない）。そのまま TtsHttpError として
      //   投げれば、audioStore が既存の経路で 503 に落とす（`server/audioStore.ts`）
      const res = await request("audio.speech", `${baseUrl}/v1/audio/speech`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          model: MODEL,
          input: text,
          voice: voiceId,
          response_format: "wav",
          speed: speedScale ?? 1.0,
          stream: false,
        }),
      });

      let wav: ArrayBuffer;
      try {
        wav = await res.arrayBuffer();
      } catch (err) {
        throw describe("audio.speech の読み取り", err);
      }

      if (!looksLikeWav(wav)) {
        throw new TtsHttpError("audio.speech", res.status, `WAV ではない応答が返りました（${wav.byteLength} バイト）`);
      }
      return wav;
    },

    async listVoices() {
      const res = await request("audio.voices", `${baseUrl}/v1/audio/voices`, { method: "GET" });
      let parsed: unknown;
      try {
        parsed = await res.json();
      } catch (err) {
        throw describe("audio.voices の読み取り", err);
      }
      const list = (parsed as { voices?: unknown } | null)?.voices;
      if (!Array.isArray(list)) throw new Error("voices が配列を返しませんでした");
      return normalizeVoices(list);
    },
  };
}
