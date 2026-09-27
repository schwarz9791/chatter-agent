/**
 * OpenAI 互換の音声合成 API（`POST /v1/audio/speech` / `GET /v1/audio/voices`）を叩く
 * `TtsEngine`（→ `tts/ttsEngine.ts`）。第一の相手は Kokoro-FastAPI
 * （`server/engineProcess.ts` の `resolveKokoroSpawn` が起こす）。
 *
 * ★ `baseUrl` は **`/v1` を含まない origin**。`ttsSpawn` が `--host`/`--port` を
 *   `ttsBaseUrl` から導出するのと揃うように、パスはこのクライアントが足す。
 *   末尾に `/` や `/v1` が書かれていても正規化してから連結する（`normalizeBaseUrl`）——
 *   さもないと `/v1/v1/audio/speech` になり、別のエンジンが応答しているのと区別が
 *   付かない 404 になる。
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

/**
 * `baseUrl` の末尾の `/` と `/v1` を落とす。**利用者が OpenAI の慣例に倣って
 * `http://host:port/v1` と書くことがある**ので、そのまま連結すると `/v1/v1/audio/speech` に
 * なって 404 が返る（別のエンジンが応答しているのと区別が付かない誤診断を招く）。
 */
function normalizeBaseUrl(raw: string): string {
  return raw.replace(/\/+$/, "").replace(/\/v1$/i, "");
}

/** 合成指定に載る重み `(2)` / `(0.5)` を落とす。`af_bella(2)` → `af_bella` */
function stripWeight(part: string): string {
  return part.replace(/\(\d+(?:\.\d+)?\)$/, "");
}

export function createOpenAiEngine(options: TtsEngineOptions): TtsEngine {
  const baseUrl = normalizeBaseUrl(options.baseUrl);
  const { voiceId, timeoutMs, speedScale } = options;
  const request = createRequester(timeoutMs);

  return {
    baseUrl,

    /**
     * Kokoro-FastAPI は複数の声を `+` で混ぜた合成指定（`af_bella+af_sky`）や、
     * 重み付き（`af_bella(2)+af_sky(1)`）を受け付ける。完全一致で見ると、
     * 合成は 200 で鳴るのに「存在しません」と誤診断する。**構成要素がすべて
     * 一覧にあれば真**（重みの大小や順序までは検査しない —— それはエンジンの仕事）。
     */
    hasVoice(voices, id) {
      const known = new Set(voices.map((voice) => voice.id));
      const parts = id
        .split("+")
        .map((part) => stripWeight(part.trim()).trim())
        .filter(Boolean);
      return parts.length > 0 && parts.every((part) => known.has(part));
    },

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
