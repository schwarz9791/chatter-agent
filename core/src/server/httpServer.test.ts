import { describe, it, expect, afterEach, vi } from "vitest";
import type * as http from "http";
import { createAudioStore } from "./audioStore";
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

function wavOf(bytes: number): ArrayBuffer {
  return new ArrayBuffer(bytes);
}

async function start(overrides: Partial<AudioHttpDeps> = {}): Promise<string> {
  const server = createAudioHttpServer({
    store: createAudioStore({ synthesize: () => Promise.resolve(wavOf(12)) }),
    lookup: (seq) => (seq === 1 ? record(1) : null),
    allowedOrigins: [],
    disabled: () => false,
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
      store: createAudioStore({ synthesize: () => Promise.reject(new Error("ECONNREFUSED")) }),
    });
    const res = await fetch(`${base}/audio/${EPOCH}-000000000001.wav`);

    expect(res.status).toBe(503);
    expect(res.headers.get("retry-after")).toBe("1");
  });

  it("★ パストラバーサルを弾く", async () => {
    const base = await start({ lookup: () => record(1) });
    for (const path of ["/audio/../../etc/passwd", "/audio/%2e%2e%2f%2e%2e%2fetc%2fpasswd", "/etc/passwd", "/"]) {
      expect((await fetch(`${base}${path}`)).status, path).toBe(404);
    }
  });

  it("GET / HEAD 以外は 405", async () => {
    const base = await start();
    expect((await fetch(`${base}/audio/${EPOCH}-000000000001.wav`, { method: "POST" })).status).toBe(405);
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

  it("Origin を送らないクライアント（Unity ネイティブなど）は素通り", async () => {
    const base = await start();
    expect((await fetch(`${base}/audio/${EPOCH}-000000000001.wav`)).status).toBe(200);
  });
});
