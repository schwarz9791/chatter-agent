/**
 * `ws` をモックせず、127.0.0.1 に実サーバーを立てて検証する（`wsServer.test.ts` と同じ方針）。
 * クライアント側の関心は「接続直後の同期送出を取りこぼさないか」「切れたら繋ぎ直すか」なので、
 * モックだと肝心のタイミングが検証できない。
 */

import { describe, it, expect, afterEach, vi } from "vitest";
import { WebSocketServer } from "ws";
import type { WebSocket as WsSocket } from "ws";
import type { AddressInfo } from "net";
import { createSpeechClient, deriveServerUrl } from "./client";
import type { SpeechClient } from "./client";

const servers: WebSocketServer[] = [];
const clients: SpeechClient[] = [];

afterEach(async () => {
  for (const client of clients.splice(0)) await client.close();
  for (const server of servers.splice(0)) {
    for (const socket of server.clients) socket.terminate();
    await new Promise<void>((done) => server.close(() => done()));
  }
  vi.restoreAllMocks();
});

interface Stub {
  url: string;
  server: WebSocketServer;
  /** 接続してきたソケット（新しい順ではなく接続順） */
  sockets: WsSocket[];
  received: string[];
}

/** 接続してきたクライアントに、connection ハンドラから**同期で**送る（server の catchUp と同じ形） */
async function stub(onConnect?: (socket: WsSocket) => void): Promise<Stub> {
  const server = new WebSocketServer({ host: "127.0.0.1", port: 0 });
  servers.push(server);
  await new Promise<void>((done) => server.once("listening", () => done()));

  const out: Stub = {
    url: `ws://127.0.0.1:${(server.address() as AddressInfo).port}`,
    server,
    sockets: [],
    received: [],
  };

  server.on("connection", (socket) => {
    out.sockets.push(socket);
    socket.on("message", (data) => out.received.push(String(data)));
    onConnect?.(socket);
  });

  return out;
}

function connect(url: string, overrides: Partial<Parameters<typeof createSpeechClient>[0]> = {}) {
  const frames: string[] = [];
  const events: string[] = [];
  const client = createSpeechClient({
    url,
    onFrame: (raw) => frames.push(raw),
    onConnected: () => events.push("connected"),
    onDisconnected: () => events.push("disconnected"),
    pingWatchdogMs: 0,
    backoffMinMs: 40,
    ...overrides,
  });
  clients.push(client);
  client.start();
  return { client, frames, events };
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

async function until(predicate: () => boolean, timeoutMs = 3000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await sleep(10);
  }
  throw new Error("条件が満たされませんでした");
}

describe("deriveServerUrl", () => {
  it("bind アドレスをそのまま接続先にしない", () => {
    // ★ 0.0.0.0 に connect すると macOS では偶然通るが、意図した挙動ではない
    expect(deriveServerUrl("0.0.0.0", 8570)).toBe("ws://127.0.0.1:8570");
    expect(deriveServerUrl("::", 8570)).toBe("ws://127.0.0.1:8570");
    expect(deriveServerUrl("", 8570)).toBe("ws://127.0.0.1:8570");
  });

  it("具体的なホストはそのまま使う", () => {
    expect(deriveServerUrl("192.168.1.10", 9000)).toBe("ws://192.168.1.10:9000");
    expect(deriveServerUrl("mac.local", 8570)).toBe("ws://mac.local:8570");
  });

  it("IPv6 リテラルは角括弧で囲む", () => {
    expect(deriveServerUrl("fe80::1", 8570)).toBe("ws://[fe80::1]:8570");
  });
});

describe("接続", () => {
  it("★ 接続直後に同期で送られたフレームを取りこぼさない", async () => {
    // server は connection ハンドラの中から catchUp を同期で流す。
    // open を待ってから message ハンドラを張ると、ここで落ちる
    const s = await stub((socket) => {
      socket.send('{"seq":1}');
      socket.send('{"seq":2}');
    });
    const { frames } = connect(s.url);
    await until(() => frames.length === 2);
    expect(frames).toEqual(['{"seq":1}', '{"seq":2}']);
  });

  it("接続すると onConnected が呼ばれる", async () => {
    const s = await stub();
    const { events } = connect(s.url);
    await until(() => events.includes("connected"));
  });
});

describe("ack", () => {
  it("累積 ack を送る", async () => {
    const s = await stub();
    const { client } = connect(s.url);
    await until(() => s.sockets.length === 1);

    client.ack(3);
    await until(() => s.received.length === 1);
    expect(JSON.parse(s.received[0])).toEqual({ type: "spoken", seq: 3 });
  });

  it("★ 短時間に何度呼んでも最大値が1回だけ飛ぶ（追いつきのバーストを畳む）", async () => {
    const s = await stub();
    const { client } = connect(s.url);
    await until(() => s.sockets.length === 1);

    for (let seq = 1; seq <= 100; seq++) client.ack(seq);
    await sleep(150);
    expect(s.received).toHaveLength(1);
    expect(JSON.parse(s.received[0])).toEqual({ type: "spoken", seq: 100 });
  });

  it("接続前の ack は接続後に送られる", async () => {
    const s = await stub();
    const { client } = connect(s.url);
    client.ack(7);
    await until(() => s.received.length === 1);
    expect(JSON.parse(s.received[0])).toEqual({ type: "spoken", seq: 7 });
  });
});

describe("再接続", () => {
  it("切断されたら繋ぎ直す", async () => {
    const s = await stub();
    const { events } = connect(s.url);
    await until(() => s.sockets.length === 1);

    s.sockets[0].close(1013, "too slow");
    await until(() => s.sockets.length === 2, 5000);
    expect(events.filter((e) => e === "disconnected")).toHaveLength(1);
    expect(events.filter((e) => e === "connected")).toHaveLength(2);
  });

  it("繋ぎ直した接続でも追いつきを取りこぼさない", async () => {
    let connections = 0;
    const s = await stub((socket) => {
      connections++;
      if (connections === 2) socket.send('{"seq":42}');
    });
    const { frames } = connect(s.url);
    await until(() => s.sockets.length === 1);
    s.sockets[0].close();
    await until(() => frames.includes('{"seq":42}'), 5000);
  });

  it("サーバーが居なくても諦めず、後から繋がる", async () => {
    // 先にポートだけ確保して解放し、そのポートへ繋ぎに行かせる
    const probe = new WebSocketServer({ host: "127.0.0.1", port: 0 });
    await new Promise<void>((done) => probe.once("listening", () => done()));
    const { port } = probe.address() as AddressInfo;
    await new Promise<void>((done) => probe.close(() => done()));

    vi.spyOn(console, "warn").mockImplementation(() => {});
    const { events } = connect(`ws://127.0.0.1:${port}`);
    await sleep(120);
    expect(events).not.toContain("connected");

    const server = new WebSocketServer({ host: "127.0.0.1", port });
    servers.push(server);
    await until(() => events.includes("connected"), 5000);
  });

  it("close の後は繋ぎ直さない", async () => {
    const s = await stub();
    const { client } = connect(s.url);
    await until(() => s.sockets.length === 1);

    await client.close();
    await sleep(200);
    expect(s.sockets).toHaveLength(1);
  });
});

describe("ping watchdog", () => {
  it("★ ping が途切れたら切り直す（half-open のまま永久に無音にしない）", async () => {
    // サーバーは 30 秒ごとに ping を打つが、その対称の仕組みがクライアントには無い。
    // スリープ復帰や NAT テーブル切れで half-open になると接続中のまま黙り込む
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const s = await stub();
    const { events } = connect(s.url, { pingWatchdogMs: 100 });
    await until(() => s.sockets.length === 1);

    // ping を一度も打たないので watchdog が発火して繋ぎ直す
    await until(() => s.sockets.length === 2, 5000);
    expect(events).toContain("disconnected");
  });

  it("ping が来ている間は切らない", async () => {
    const s = await stub();
    connect(s.url, { pingWatchdogMs: 150 });
    await until(() => s.sockets.length === 1);

    const pinger = setInterval(() => s.sockets[0]?.ping(), 50);
    await sleep(500);
    clearInterval(pinger);
    expect(s.sockets).toHaveLength(1);
  });
});
