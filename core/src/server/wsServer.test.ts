/**
 * 実ポートを開いて検証する。ws をモックすると「想像した ws の API」しか検証できないため。
 * 0.0.0.0 ではなく 127.0.0.1 を使うのは、macOS のローカルネットワーク許可ダイアログを引かないため。
 * port: 0 で ephemeral port を割り当てるので、並行実行しても衝突しない。
 */

import { describe, it, expect, afterEach, vi } from "vitest";
import { WebSocket } from "ws";
import { createWsServer, parseAck } from "./wsServer";
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
 *   `onConnect` の送り直しは connection ハンドラで同期に流れるので、
 *   open を待ってから張ると取りこぼす。実クライアントも同じ順序で書く必要がある。
 */
function connect(server: WsServer, options: { headers?: Record<string, string> } = {}): Promise<Client> {
  const { port } = server.address();
  const socket = new WebSocket(`ws://${HOST}:${port}`, { headers: options.headers });
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

describe("parseAck", () => {
  it("累積 ack を読む", () => {
    expect(parseAck('{"type":"spoken","seq":12}')).toBe(12);
    expect(parseAck('{"type":"spoken","seq":0}')).toBe(0);
  });

  it("★ 知らない形は捨てる（ここはクライアント由来の入力）", () => {
    for (const raw of [
      "",
      "not json",
      "[]",
      "null",
      '"spoken"',
      "{}",
      '{"seq":1}',
      '{"type":"other","seq":1}',
      '{"type":"spoken"}',
      '{"type":"spoken","seq":"1"}',
      '{"type":"spoken","seq":-1}',
      '{"type":"spoken","seq":1.5}',
      '{"type":"spoken","seq":null}',
      '{"type":"spoken","seq":1e400}',
      `{"type":"spoken","seq":${Number.MAX_SAFE_INTEGER + 10}}`,
    ]) {
      expect(parseAck(raw)).toBeNull();
    }
  });
});

describe("接続直後の追いつき", () => {
  it("onConnect で渡した分が、以後のブロードキャストより先に届く", async () => {
    const server = await start({ onConnect: (send) => void send("catchup") });
    const client = await connect(server);

    server.broadcast("live");
    expect(await client.waitFor(2)).toEqual(["catchup", "live"]);
  });

  it("onConnect が投げても接続は生き続ける", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const server = await start({
      onConnect: () => {
        throw new Error("読めなかった");
      },
    });
    const client = await connect(server);

    server.broadcast("live");
    expect(await client.waitFor(1)).toEqual(["live"]);
  });
});

describe("ack", () => {
  it("クライアントの ack が届く", async () => {
    const acks: number[] = [];
    const server = await start({ onAck: (seq) => acks.push(seq) });
    const client = await connect(server);

    client.socket.send('{"type":"spoken","seq":7}');
    await vi.waitFor(() => expect(acks).toEqual([7]));
  });

  it("★ 不正な ack はハンドラまで届かない", async () => {
    const acks: number[] = [];
    const server = await start({ onAck: (seq) => acks.push(seq) });
    const client = await connect(server);

    client.socket.send('{"type":"spoken","seq":-1}');
    client.socket.send("not json");
    client.socket.send('{"type":"mute"}');
    client.socket.send('{"type":"spoken","seq":3}');

    await vi.waitFor(() => expect(acks).toEqual([3]));
  });

  it("★ onAck が投げても接続は生き続ける（message イベント経由の uncaught でサーバーごと落ちない）", async () => {
    vi.spyOn(console, "error").mockImplementation(() => {});
    const server = await start({
      onAck: () => {
        throw new Error("キューを読めなかった");
      },
    });
    const client = await connect(server);

    client.socket.send('{"type":"spoken","seq":1}');
    // 例外を投げた後でも broadcast が届く＝ソケットもプロセスも生きている
    server.broadcast("live");
    expect(await client.waitFor(1)).toEqual(["live"]);
  });
});

describe("Origin 検査（#3）", () => {
  it("★ allowedOrigins が空なら Origin ヘッダ付きの接続を拒否する（既定・既存挙動の維持）", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const server = await start();

    await expect(connect(server, { headers: { Origin: "https://evil.example.com" } })).rejects.toThrow();
    expect(server.clientCount()).toBe(0);
  });

  it("Origin を送らない接続は、allowedOrigins の中身に関わらず通す（Unity / ネイティブ）", async () => {
    const server = await start({ allowedOrigins: ["http://localhost:5173"] });
    await connect(server);
    expect(server.clientCount()).toBe(1);
  });
});

describe("allowedOrigins（Electron / Unity WebGL 向け許可リスト）", () => {
  it("allowedOrigins に載っている Origin は通る", async () => {
    const server = await start({ allowedOrigins: ["http://localhost:5173"] });
    await connect(server, { headers: { Origin: "http://localhost:5173" } });
    expect(server.clientCount()).toBe(1);
  });

  it("★ allowedOrigins に載っていない Origin は拒否される", async () => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const server = await start({ allowedOrigins: ["http://localhost:5173"] });

    await expect(connect(server, { headers: { Origin: "http://localhost:9999" } })).rejects.toThrow();
    expect(server.clientCount()).toBe(0);
  });
});

describe("maxPayload（4KB超）", () => {
  it("★ 4KBを超えるフレームを送ると接続が 1009 で切れる", async () => {
    const server = await start();
    const client = await connect(server);

    const closeCode = new Promise<number>((resolve) => {
      client.socket.once("close", (code) => resolve(code));
    });

    client.socket.send("a".repeat(5 * 1024));
    expect(await closeCode).toBe(1009);
  });
});

describe("バックプレッシャ（sendTo）", () => {
  it("★ bufferedAmount 超過で接続を切る（フレームを捨てて繋ぎっぱなしにはしない）", async () => {
    // 実ソケットで bufferedAmount を 1MB 超えさせるのは決定的でないため、maxBufferedBytes に
    // 負の値を注入して `socket.bufferedAmount > maxBuffered`（0 > -1）を常に真にする。
    // テスト専用の踏み方
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const server = await start({ maxBufferedBytes: -1 });
    const client = await connect(server);

    let received = false;
    client.socket.once("message", () => {
      received = true;
    });
    const closeCode = new Promise<number>((resolve) => {
      client.socket.once("close", (code) => resolve(code));
    });

    server.broadcast("hello");

    expect(await closeCode).toBe(1013);
    expect(received).toBe(false);
  });
});
