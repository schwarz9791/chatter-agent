/**
 * `voicevoxClient.test.ts` と同じ方針で、`fetch` をモックせず実サーバーを立てて検証する。
 */

import { describe, it, expect, afterEach } from "vitest";
import * as http from "http";
import type { AddressInfo } from "net";
import { createOpenAiEngine } from "./openaiClient";
import { TtsHttpError, TtsTransportError } from "./ttsEngine";

type Handler = (req: http.IncomingMessage, res: http.ServerResponse) => void;

const servers: http.Server[] = [];

afterEach(async () => {
  for (const server of servers.splice(0)) {
    await new Promise<void>((done) => server.close(() => done()));
  }
});

async function serve(handler: Handler): Promise<string> {
  const server = http.createServer(handler);
  servers.push(server);
  await new Promise<void>((done) => server.listen(0, "127.0.0.1", () => done()));
  const { port } = server.address() as AddressInfo;
  return `http://127.0.0.1:${port}`;
}

function readBody(req: http.IncomingMessage): Promise<string> {
  return new Promise((resolve) => {
    let body = "";
    req.on("data", (chunk) => (body += chunk));
    req.on("end", () => resolve(body));
  });
}

function client(baseUrl: string, opts: { voiceId?: string; speedScale?: number; timeoutMs?: number } = {}) {
  return createOpenAiEngine({
    baseUrl,
    voiceId: opts.voiceId ?? "af_heart",
    speedScale: opts.speedScale,
    timeoutMs: opts.timeoutMs ?? 2000,
  });
}

/** RIFF/WAVE の最小ヘッダ。`synthesize` が中身を検証するので4バイトでは足りない */
const WAV_HEAD = Buffer.concat([Buffer.from("RIFF"), Buffer.alloc(4), Buffer.from("WAVE")]);

describe("synthesize", () => {
  it("POST /v1/audio/speech に model/voice/response_format/speed/stream を積んで WAV を返す", async () => {
    let seen: { url: string; body: unknown } | undefined;
    const baseUrl = await serve((req, res) => {
      void readBody(req).then((body) => {
        seen = { url: req.url ?? "", body: JSON.parse(body) };
        res.writeHead(200, { "Content-Type": "audio/wav" });
        res.end(WAV_HEAD);
      });
    });

    const wav = await client(baseUrl, { speedScale: 1.5 }).synthesize("hello");
    expect(Buffer.from(wav).subarray(0, 4).toString("latin1")).toBe("RIFF");
    expect(seen?.url).toBe("/v1/audio/speech");
    expect(seen?.body).toEqual({
      model: "tts-1",
      input: "hello",
      voice: "af_heart",
      response_format: "wav",
      speed: 1.5,
      stream: false,
    });
  });

  /** ★ VOICEVOX 系（`voicevoxClient.ts`）の「省略は触らない」とは逆で、常に明示の値を送る */
  it("★ speedScale を省略すると 1.0 を明示で送る", async () => {
    let seenSpeed: unknown;
    const baseUrl = await serve((req, res) => {
      void readBody(req).then((body) => {
        seenSpeed = (JSON.parse(body) as { speed: unknown }).speed;
        res.writeHead(200, { "Content-Type": "audio/wav" });
        res.end(WAV_HEAD);
      });
    });

    await client(baseUrl).synthesize("hello");
    expect(seenSpeed).toBe(1.0);
  });

  it("★ 200 でも WAV でなければ弾く", async () => {
    const baseUrl = await serve((req, res) => {
      req.resume();
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end("{}");
    });

    const result = await client(baseUrl)
      .synthesize("hello")
      .catch((err: unknown) => err);
    expect(result).toBeInstanceOf(TtsHttpError);
    expect((result as Error).message).toContain("WAV ではない");
  });

  /** ★ 存在しない声・空の入力はどちらも 400（404 ではない）。detail を保つ */
  it("★ 400 は detail を保ったまま TtsHttpError になる", async () => {
    const baseUrl = await serve((req, res) => {
      req.resume();
      res.writeHead(400, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ detail: { error: "validation_error", message: "Voice 'xx' not found" } }));
    });

    const result = await client(baseUrl)
      .synthesize("hello")
      .catch((err: unknown) => err);
    expect(result).toBeInstanceOf(TtsHttpError);
    expect((result as TtsHttpError).status).toBe(400);
    expect((result as TtsHttpError).detail).toContain("Voice 'xx' not found");
  });

  it("繋がらない相手は TtsTransportError になる", async () => {
    const dead = client("http://127.0.0.1:1", { timeoutMs: 1000 });
    await expect(dead.synthesize("hello")).rejects.toBeInstanceOf(TtsTransportError);
  });
});

describe("listVoices", () => {
  it("{id, name} 形式を受ける（label には id を入れる）", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(
        JSON.stringify({
          voices: [
            { id: "af_heart", name: "af_heart" },
            { id: "am_michael", name: "am_michael" },
          ],
        }),
      );
    });
    expect(await client(baseUrl).listVoices()).toEqual([
      { id: "af_heart", label: "af_heart" },
      { id: "am_michael", label: "am_michael" },
    ]);
  });

  it("文字列だけの配列も受ける", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ voices: ["af_heart", "bf_emma"] }));
    });
    expect(await client(baseUrl).listVoices()).toEqual([
      { id: "af_heart", label: "af_heart" },
      { id: "bf_emma", label: "bf_emma" },
    ]);
  });

  it("voices が配列でなければ例外", async () => {
    const baseUrl = await serve((_req, res) => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ voices: "nope" }));
    });
    await expect(client(baseUrl).listVoices()).rejects.toThrow("配列を返しませんでした");
  });
});
