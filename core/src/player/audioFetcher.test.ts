import { describe, it, expect, afterEach } from "vitest";
import * as http from "http";
import { createAudioFetcher, deriveAudioBaseUrl } from "./audioFetcher";

const HOST = "127.0.0.1";
const PATH = "/audio/gen-1-000000000001.wav";

const servers: http.Server[] = [];

afterEach(async () => {
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

async function start(handler: http.RequestListener): Promise<string> {
  const server = http.createServer(handler);
  servers.push(server);
  await new Promise<void>((done) => server.listen(0, HOST, done));
  const address = server.address();
  if (address === null || typeof address === "string") throw new Error("bind に失敗しました");
  return `http://${HOST}:${address.port}`;
}

describe("deriveAudioBaseUrl", () => {
  it("ws → http、wss → https に読み替える", () => {
    expect(deriveAudioBaseUrl("ws://127.0.0.1:8570")).toBe("http://127.0.0.1:8570");
    expect(deriveAudioBaseUrl("wss://mascot.example.com")).toBe("https://mascot.example.com");
  });

  it("LAN 越しでも接続先の authority をそのまま使う（サーバーは自分の到達先を知らない）", () => {
    expect(deriveAudioBaseUrl("ws://192.168.1.5:8570")).toBe("http://192.168.1.5:8570");
  });
});

describe("fetchAudio", () => {
  it("200 なら ready", async () => {
    const base = await start((_req, res) => {
      res.writeHead(200, { "content-type": "audio/wav" });
      res.end(Buffer.alloc(9));
    });
    const result = await createAudioFetcher({ baseUrl: base, timeoutMs: 2000 }).fetchAudio(PATH);

    expect(result.kind).toBe("ready");
    if (result.kind === "ready") expect(result.wav.byteLength).toBe(9);
  });

  it("★ 503 は unavailable（試行回数を消費させないための区別）", async () => {
    const base = await start((_req, res) => {
      res.writeHead(503).end("synthesis unavailable\n");
    });
    expect((await createAudioFetcher({ baseUrl: base, timeoutMs: 2000 }).fetchAudio(PATH)).kind).toBe("unavailable");
  });

  it("★ 404 は gone（諦めて ack する）", async () => {
    const base = await start((_req, res) => {
      res.writeHead(404).end("not found\n");
    });
    expect((await createAudioFetcher({ baseUrl: base, timeoutMs: 2000 }).fetchAudio(PATH)).kind).toBe("gone");
  });

  it("想定外のステータスは failed", async () => {
    const base = await start((_req, res) => {
      res.writeHead(500).end("boom\n");
    });
    expect((await createAudioFetcher({ baseUrl: base, timeoutMs: 2000 }).fetchAudio(PATH)).kind).toBe("failed");
  });

  it("繋がらなければ failed", async () => {
    // ポート 1 は使われていない（voicevoxClient.test.ts と同じ手）
    const result = await createAudioFetcher({ baseUrl: "http://127.0.0.1:1", timeoutMs: 2000 }).fetchAudio(PATH);
    expect(result.kind).toBe("failed");
  });

  it("★ 返らない相手はタイムアウトで failed（掴んだままだと以後すべてが無音になる）", async () => {
    const base = await start(() => {
      /* 応答しない */
    });
    const result = await createAudioFetcher({ baseUrl: base, timeoutMs: 200 }).fetchAudio(PATH);

    expect(result.kind).toBe("failed");
    if (result.kind === "failed") expect(result.reason).toContain("200ms");
  });
});
