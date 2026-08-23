import { describe, it, expect, afterEach, vi } from "vitest";
import type * as http from "http";
import { createAudioStore } from "./audioStore";
import { TtsHttpError } from "../tts/voicevoxClient";
import { createAudioHttpServer, type AudioHttpDeps } from "./httpServer";
import type { SpeechRecord } from "../core/types";

const HOST = "127.0.0.1";
const EPOCH = "gen-1";

const servers: http.Server[] = [];

afterEach(async () => {
  vi.restoreAllMocks();
  await Promise.all(
    servers.splice(0).map(
      (server) =>
        new Promise<void>((done) => {
          server.closeAllConnections();
          server.close(() => done());
        }),
    ),
  );
});

function record(seq: number, text = `文${seq}。`, epoch = EPOCH): SpeechRecord {
  return {
    epoch,
    seq,
    ts: "2026-08-15T00:00:00.000Z",
    source: "claude-code",
    sessionId: null,
    turnId: null,
    messageId: null,
    kind: "assistant",
    text,
    emotion: "neutral",
  };
}

const VOICE = { baseUrl: "http://127.0.0.1:10101", speakerId: 888753760 };

function wavOf(bytes: number): ArrayBuffer {
  return new ArrayBuffer(bytes);
}

async function start(overrides: Partial<AudioHttpDeps> = {}): Promise<string> {
  const server = createAudioHttpServer({
    store: createAudioStore({ currentVoice: () => VOICE, synthesize: () => Promise.resolve(wavOf(12)) }),
    lookup: (seq) => (seq === 1 ? record(1) : null),
    allowedOrigins: [],
    disabled: () => false,
    responseTimeoutMs: 5_000,
    ...overrides,
  });
  servers.push(server);
  await new Promise<void>((done) => server.listen(0, HOST, done));
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("bind に失敗しました");
  return `http://${HOST}:${address.port}`;
}

describe("GET /audio/<epoch>-<seq>.wav", () => {
  it("合成して WAV を返す", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);

    expect(res.status).toBe(200);
    expect(res.headers.get("content-type")).toBe("audio/wav");
    expect((await res.arrayBuffer()).byteLength).toBe(12);
  });

  it("Range は実装しない（Unity / ExoPlayer が投げてきても全体を返す）", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { headers: { range: "bytes=0-3" } });

    expect(res.status).toBe(200);
    expect(res.headers.get("accept-ranges")).toBe("none");
    expect((await res.arrayBuffer()).byteLength).toBe(12);
  });

  it("★ キューから消えた entry は 404（永久に用意できない）", async () => {
    const base = await start({ lookup: () => null });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(res.status).toBe(404);
  });

  it("★ 世代違いの URL は 404（採番のやり直しを跨いだ古い URL）", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/gen-9-000000000001.wav`);
    expect(res.status).toBe(404);
  });

  it("読み上げる中身が無い文は 404（約物だけの断片）", async () => {
    const base = await start({ lookup: () => record(1, "！") });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(res.status).toBe(404);
  });

  it("ttsEnabled=false なら 404", async () => {
    const base = await start({ disabled: () => true });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(res.status).toBe(404);
  });

  it("★ 合成できないときは 503 + Retry-After（404 と混ぜない）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start({
      store: createAudioStore({
        currentVoice: () => VOICE,
        synthesize: () => Promise.reject(new Error("ECONNREFUSED 127.0.0.1:10101")),
      }),
    });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);

    expect(res.status).toBe(503);
    expect(res.headers.get("retry-after")).toBe("1");
    // ★ 理由を本文にも載せる。無音の原因はログにしか出ないので、クライアント側にも渡す
    expect(await res.text()).toContain("ECONNREFUSED");
  });

  it("★ エンジンが 4xx を返しても 503（404 にすると本文が物理削除される）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start({
      store: createAudioStore({
        currentVoice: () => VOICE,
        synthesize: () => Promise.reject(new TtsHttpError("audio_query", 422, "speaker not found")),
      }),
    });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);

    expect(res.status).toBe(503);
    expect(await res.text()).toContain("speaker not found");
  });

  it("合成が失敗したら onSynthesisFailed が呼ばれる（診断の再実行）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const onSynthesisFailed = vi.fn();
    const base = await start({
      store: createAudioStore({ currentVoice: () => VOICE, synthesize: () => Promise.reject(new Error("down")) }),
      onSynthesisFailed,
    });
    await (await fetch(`${base}/audio/${EPOCH}-000000000001.wav`)).text();

    expect(onSynthesisFailed).toHaveBeenCalled();
  });

  it("★ 応答だけ期限で打ち切り、合成は走らせたままにする（次の GET はキャッシュに当たる）", async () => {
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => {});
    let resolveSynth: ((wav: ArrayBuffer) => void) | undefined;
    const synthesize = vi.fn(
      () =>
        new Promise<ArrayBuffer>((resolve) => {
          resolveSynth = resolve;
        }),
    );
    const base = await start({
      store: createAudioStore({ currentVoice: () => VOICE, synthesize }),
      responseTimeoutMs: 150,
    });

    const first = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(first.status).toBe(503);
    expect(await first.text()).toContain("in progress");
    expect(warnSpy).toHaveBeenCalled();

    // 合成は打ち切られていないので、終わればキャッシュに入る
    resolveSynth?.(wavOf(9));
    await new Promise((r) => setTimeout(r, 20));

    const second = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);
    expect(second.status).toBe(200);
    expect((await second.arrayBuffer()).byteLength).toBe(9);
    // 打ち切っても合成をやり直していない
    expect(synthesize).toHaveBeenCalledTimes(1);
  });

  it("★ 503 の warn は間引かれる（先読み窓 × 1秒で1日34万行になる）", async () => {
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start({
      store: createAudioStore({ currentVoice: () => VOICE, synthesize: () => Promise.reject(new Error("down")) }),
    });

    for (let i = 0; i < 10; i++) {
      await (await fetch(`${base}/audio/${EPOCH}-000000000001.wav`)).text();
    }

    expect(warnSpy.mock.calls.length).toBe(1);
  });

  it("★ パストラバーサルを弾く", async () => {
    const base = await start({ lookup: () => record(1) });
    for (const path of ["/audio/../../etc/passwd", "/audio/%2e%2e%2f%2e%2e%2fetc%2fpasswd", "/etc/passwd", "/"]) {
      expect((await fetch(`${base}${path}`)).status, path).toBe(404);
    }
  });

  it("GET / HEAD / OPTIONS 以外は 405 で、Allow を返す（RFC 9110 §15.5.6）", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { method: "POST" });
    expect(res.status).toBe(405);
    expect(res.headers.get("allow")).toBe("GET, HEAD, OPTIONS");
  });

  it("★ OPTIONS は 204 + Allow-Methods（405 で切るとプリフライトが通らない）", async () => {
    const base = await start({ allowedOrigins: ["tauri://localhost"] });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, {
      method: "OPTIONS",
      headers: { origin: "tauri://localhost" },
    });

    expect(res.status).toBe(204);
    expect(res.headers.get("access-control-allow-methods")).toBe("GET, HEAD, OPTIONS");
    expect(res.headers.get("access-control-allow-origin")).toBe("tauri://localhost");
    // ★ Range は実装しないので、対応していると言わない
    expect(res.headers.get("access-control-allow-headers")).toBeNull();
  });

  it("プリフライトも Origin 検査を通る", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, {
      method: "OPTIONS",
      headers: { origin: "http://evil.example.com" },
    });
    expect(res.status).toBe(403);
  });

  it("HEAD は本体を返さずヘッダだけ返す", async () => {
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { method: "HEAD" });
    expect(res.status).toBe(200);
    expect(res.headers.get("content-length")).toBe("12");
  });
});

describe("Origin（WebSocket と同じ規則）", () => {
  it("許可リストに無い Origin は 403", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start();
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, {
      headers: { origin: "http://evil.example.com" },
    });
    expect(res.status).toBe(403);
  });

  it("★ 許可した Origin には Access-Control-Allow-Origin を返す（403 を返さないだけでは足りない）", async () => {
    const base = await start({ allowedOrigins: ["tauri://localhost"] });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { headers: { origin: "tauri://localhost" } });

    expect(res.status).toBe(200);
    // これが無いと、サーバーが許可していてもブラウザ側で音声だけブロックされる
    expect(res.headers.get("access-control-allow-origin")).toBe("tauri://localhost");
    expect(res.headers.get("vary")).toBe("Origin");
  });

  it("★ Retry-After を expose する（safelist に無いのでブラウザ / WebView の JS から読めない）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const base = await start({
      allowedOrigins: ["tauri://localhost"],
      store: createAudioStore({
        currentVoice: () => VOICE,
        synthesize: () => Promise.reject(new Error("ECONNREFUSED")),
      }),
    });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { headers: { origin: "tauri://localhost" } });
    await res.text();

    // #29 が主目的にしている XR / Unity WebGL / Electron renderer は、これが無いと
    // 503 のバックオフ情報を受け取れない
    expect(res.status).toBe(503);
    expect(res.headers.get("access-control-expose-headers")).toBe("retry-after");
  });

  it("Origin を送らないクライアント（Unity ネイティブなど）は素通り", async () => {
    const base = await start();
    expect((await fetch(`${base}/audio/${EPOCH}-000000000001.wav`)).status).toBe(200);
  });
});
