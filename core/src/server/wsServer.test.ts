/**
 * 実ポートを開いて検証する。ws をモックすると「想像した ws の API」しか検証できないため。
 * 0.0.0.0 ではなく 127.0.0.1 を使うのは、macOS のローカルネットワーク許可ダイアログを引かないため。
 * port: 0 で ephemeral port を割り当てるので、並行実行しても衝突しない。
 */

import { describe, it, expect, afterEach, vi } from "vitest";
import { WebSocket } from "ws";
import { createWsServer, parseSince } from "./wsServer";
import type { WsServer, WsServerOptions } from "./wsServer";

const HOST = "127.0.0.1";
const servers: WsServer[] = [];
const sockets: WebSocket[] = [];

async function start(overrides: Partial<WsServerOptions> = {}): Promise<WsServer> {
  const server = await createWsServer({ host: HOST, port: 0, heartbeatIntervalMs: 0, ...overrides });
  servers.push(server);
  return server;
}

interface Client {
  socket: WebSocket;
  /** 指定本数を受け取るまで待つ */
  waitFor(count: number): Promise<string[]>;
}

/**
 * ★ message リスナーを**接続の前**に張る。
 *   `?since=` の送り直しは connection ハンドラで同期に流れるので、
 *   open を待ってから張ると取りこぼす。実クライアントも同じ順序で書く必要がある。
 */
function connect(server: WsServer, query = ""): Promise<Client> {
  const { port } = server.address();
  const socket = new WebSocket(`ws://${HOST}:${port}${query}`);
  sockets.push(socket);

  const received: string[] = [];
  let waiter: { count: number; resolve: (lines: string[]) => void } | null = null;

  socket.on("message", (data) => {
    received.push(String(data));
    if (waiter && received.length >= waiter.count) {
      waiter.resolve(received.slice(0, waiter.count));
      waiter = null;
    }
  });

  const client: Client = {
    socket,
    waitFor(count) {
      if (received.length >= count) return Promise.resolve(received.slice(0, count));
      return new Promise((resolve) => {
        waiter = { count, resolve };
      });
    },
  };

  return new Promise((resolve, reject) => {
    socket.once("open", () => resolve(client));
    socket.once("error", reject);
  });
}

afterEach(async () => {
  for (const socket of sockets.splice(0)) socket.close();
  for (const server of servers.splice(0)) await server.close();
  vi.restoreAllMocks();
});

describe("createWsServer", () => {
  it("listening してから解決し、割り当てられたポートを返す", async () => {
    const server = await start();
    expect(server.address().port).toBeGreaterThan(0);
    expect(server.address().host).toBe(HOST);
  });

  it("クライアントが broadcast を受け取る", async () => {
    const server = await start();
    const client = await connect(server);

    server.broadcast('{"seq":1,"text":"あ。"}');
    expect(await client.waitFor(1)).toEqual(['{"seq":1,"text":"あ。"}']);
  });

  it("複数クライアント全員に届く", async () => {
    const server = await start();
    const a = await connect(server);
    const b = await connect(server);

    server.broadcast("hello");
    expect(await a.waitFor(1)).toEqual(["hello"]);
    expect(await b.waitFor(1)).toEqual(["hello"]);
    expect(server.clientCount()).toBe(2);
  });

  it("クライアントが0件でも throw しない", async () => {
    const server = await start();
    expect(() => server.broadcast("x")).not.toThrow();
  });

  it("close 後に同じポートを再 listen できる", async () => {
    const first = await createWsServer({ host: HOST, port: 0, heartbeatIntervalMs: 0 });
    const { port } = first.address();
    await first.close();

    const second = await createWsServer({ host: HOST, port, heartbeatIntervalMs: 0 });
    servers.push(second);
    expect(second.address().port).toBe(port);
  });

  it("使用中のポートは EADDRINUSE で reject する（監視を始める前に落ちる）", async () => {
    const server = await start();
    const { port } = server.address();
    await expect(createWsServer({ host: HOST, port, heartbeatIntervalMs: 0 })).rejects.toMatchObject({
      code: "EADDRINUSE",
    });
  });
});

describe("parseSince", () => {
  it("?since= を読む", () => {
    expect(parseSince("/?since=12")).toBe(12);
    expect(parseSince("/path?since=0")).toBe(0);
  });

  it("無い・不正なものは null", () => {
    expect(parseSince(undefined)).toBeNull();
    expect(parseSince("/")).toBeNull();
    expect(parseSince("/?other=1")).toBeNull();
    expect(parseSince("/?since=abc")).toBeNull();
    expect(parseSince("/?since=-1")).toBeNull();
    expect(parseSince("/?since=1.5")).toBeNull();
  });

  it("★ 空文字は null（全履歴リプレイにしない）", () => {
    // Number("") は 0 なので、素直に Number() へ渡すと「まだ何も受け取っていない」つもりの
    // クライアントにログ全体を送りつけてしまう
    expect(parseSince("/?since=")).toBeNull();
    expect(parseSince("/?since=%20%20")).toBeNull();
  });

  it("★ 10進数字以外の表記は受けない", () => {
    expect(parseSince("/?since=0x10")).toBeNull();
    expect(parseSince("/?since=1e3")).toBeNull();
    expect(parseSince("/?since=+5")).toBeNull();
  });
});

describe("?since= による取りこぼし埋め", () => {
  const lines = [1, 2, 3].map((seq) => JSON.stringify({ seq, text: `文${seq}。` }));

  it("接続時に seq > since の行を送り直す", async () => {
    const server = await start({ backfill: (since) => lines.slice(since) });
    const client = await connect(server, "/?since=1");
    expect(await client.waitFor(2)).toEqual([lines[1], lines[2]]);
  });

  it("?since= が無ければ何も送り直さない", async () => {
    const backfill = vi.fn(() => lines);
    const server = await start({ backfill });
    const client = await connect(server);

    server.broadcast("live");
    expect(await client.waitFor(1)).toEqual(["live"]);
    expect(backfill).not.toHaveBeenCalled();
  });

  it("送り直した分は、以後のブロードキャストより先に届く", async () => {
    const server = await start({ backfill: () => [lines[0]!] });
    const client = await connect(server, "/?since=0");

    server.broadcast("live");
    expect(await client.waitFor(2)).toEqual([lines[0], "live"]);
  });

  it("backfill が投げても接続は生き続ける", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const server = await start({
      backfill: () => {
        throw new Error("読めなかった");
      },
    });
    const client = await connect(server, "/?since=1");

    server.broadcast("live");
    expect(await client.waitFor(1)).toEqual(["live"]);
  });
});
