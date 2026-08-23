/**
 * `ws` をモックせず、127.0.0.1 に実サーバーを立てて検証する（`wsServer.test.ts` と同じ方針）。
 * クライアント側の関心は「接続直後の同期送出を取りこぼさないか」「切れたら繋ぎ直すか」なので、
 * モックだと肝心のタイミングが検証できない。
 */

import { describe, it, expect, afterEach, vi } from "vitest";
import * as http from "http";
import { WebSocketServer } from "ws";
import type { WebSocket as WsSocket } from "ws";
import type { AddressInfo } from "net";
import { createSpeechClient, deriveServerUrl } from "./client";
import type { SpeechClient } from "./client";

const servers: WebSocketServer[] = [];
const httpServers: http.Server[] = [];
const clients: SpeechClient[] = [];

afterEach(async () => {
  for (const client of clients.splice(0)) await client.close();
  for (const server of servers.splice(0)) {
    for (const socket of server.clients) socket.terminate();
    await new Promise<void>((done) => server.close(() => done()));
  }
  for (const server of httpServers.splice(0)) {
    server.closeAllConnections();
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

const EPOCH = "test-epoch";

describe("ack", () => {
  // ★ このブロックは s.sockets.length ではなく events（"connected"）を待つ。
  //   client.ts の flushAck は `if (socket?.readyState !== WebSocket.OPEN) return;` より
  //   **前**に `if (ackTimer) clearTimeout(ackTimer); ackTimer = null;` を実行する。
  //   つまりソケットがまだ OPEN でないタイミングで ACK_FLUSH_MS（20ms）のタイマーが
  //   発火すると、再スケジュールされないまま捨てられ、次に ack() が呼ばれるまで
  //   pendingAck が止まる。ack() は `if (ackTimer) return;` で早期 return するので、
  //   2度目の ack() が呼ばれれば自己回復するが、このブロックのテストはいずれも
  //   2度目の ack() を呼ばないため自己回復しない（dfbdabf で直した再接続の flaky と
  //   違って決定的に失敗する）。サーバーの connection ハンドラはハンドシェイクを
  //   受理した時点で走るが、クライアントの "connected" はその応答が届いてから出るので、
  //   クライアント側のイベントを待てばこの窓は構造的に閉じる
  it("累積 ack を送る", async () => {
    const s = await stub();
    const { client, events } = connect(s.url);
    await until(() => events.includes("connected"));

    client.ack(3, EPOCH);
    await until(() => s.received.length === 1);
    expect(JSON.parse(s.received[0])).toEqual({ type: "spoken", seq: 3, epoch: EPOCH });
  });

  it("★ 短時間に何度呼んでも最大値が1回だけ飛ぶ（追いつきのバーストを畳む）", async () => {
    const s = await stub();
    const { client, events } = connect(s.url);
    // ★ サーバー側ではなくクライアント側の connected を待つ（理由は describe 直下のコメント）
    await until(() => events.includes("connected"));

    for (let seq = 1; seq <= 100; seq++) client.ack(seq, EPOCH);
    await sleep(150);
    expect(s.received).toHaveLength(1);
    expect(JSON.parse(s.received[0])).toEqual({ type: "spoken", seq: 100, epoch: EPOCH });
  });

  it("★ dropPendingAck が間引き中の ack を捨てる", async () => {
    // 採番のやり直しは切断を伴わない。20ms のバッファに旧エポックの ack が残っていると、
    // 同じソケット上でそれが飛び、まだ喋っていない entry が消える
    const s = await stub();
    const { client, events } = connect(s.url);
    // ★ サーバー側ではなくクライアント側の connected を待つ（理由は describe 直下のコメント）
    await until(() => events.includes("connected"));

    client.ack(500, EPOCH);
    client.dropPendingAck();
    await sleep(150);
    expect(s.received).toEqual([]);

    // 捨てた後も次の ack は普通に送れる
    client.ack(1, EPOCH);
    await until(() => s.received.length === 1);
    expect(JSON.parse(s.received[0])).toEqual({ type: "spoken", seq: 1, epoch: EPOCH });
  });

  // ★ このテストだけ it() の第3引数に自前の予算（12_000）を持たせている。until に渡す期限は、
  //   テスト全体の予算に収まって初めて意味を持つ。core/vitest.config.ts は testTimeout を
  //   設定していないため既定は 5000ms だが、このテストは下の connect() に backoffMinMs: 2000 を
  //   渡していて、再接続の遅延（base/2 + jitter で 1000〜2000ms）だけで既定予算の半分近くを
  //   使ってしまい、そもそも収まらない。数字を大きくするときは、この第3引数と下の until の
  //   期限を両方動かすこと。片方だけでは守られない
  it("★ 送れなかった ack を捨てず、かつ再接続の open で勝手に送らない", async () => {
    // 捨てると reducer 側からも消えていて復旧手段が無くなる。かといって open で流すと、
    // ランタイムルートが作り直された先の**新しいサーバー**へ旧エポックの ack が飛び、
    // 配信済み・未発話の entry がまとめて消える。`dropPendingAck` は最初のフレームを
    // 見るまで出ないので、open で流す実装では構造的に間に合わない
    vi.spyOn(console, "warn").mockImplementation(() => {});
    const s = await stub();
    // 再接続を遅らせて、切断中に ack が溜まる窓を確実に作る
    const { client, events } = connect(s.url, { backoffMinMs: 2000 });
    // ★ サーバー側ではなくクライアント側の connected を待つ（理由は describe 直下のコメント）
    await until(() => events.includes("connected"));

    s.sockets[0].terminate();
    await sleep(50);
    client.ack(500, EPOCH);
    // 間引きタイマー（20ms）は発火済み。送れないので client 側に残る
    await sleep(100);
    expect(s.received).toEqual([]);

    // 繋ぎ直しても、フレームを1つも受けていないうちは送らない。
    // 待っているのは1回目の再接続で、遅延の上限は backoffMinMs: 2000 から来る
    // 1000〜2000ms（ジッタ込み）＋ハンドシェイクなので、5000 あれば最大遅延の 2.5倍の余裕がある
    await until(() => s.sockets.length === 2, 5000);
    await sleep(200);
    expect(s.received).toEqual([]);

    // 次に ack が出た時点で、溜めていた最大値ごと送られる（累積 ack なので取りこぼさない）。
    // ここで待っているのは ack() 直後の間引きタイマー（ACK_FLUSH_MS = 20ms）の発火だけなので、2000 で足りる
    client.ack(501, EPOCH);
    await until(() => s.received.length === 1, 2000);
    expect(JSON.parse(s.received[0])).toEqual({ type: "spoken", seq: 501, epoch: EPOCH });
  }, 12_000);
});

describe("再接続", () => {
  it("切断されたら繋ぎ直す", async () => {
    const s = await stub();
    const { events } = connect(s.url);
    // ★ ここも `s.sockets.length === 1` ではなくクライアント側の "connected" を待つこと。
    //   onDisconnected は client.ts の close ハンドラから**無条件に**（open を経ていなくても）
    //   発火する。サーバーの connection ハンドラはハンドシェイクを**受理した**時点で走るが、
    //   クライアントの "connected" はその応答がクライアントに届いてから出るので、サーバーが
    //   close(1013) を送るのがクライアントの open より先になり得る。そうなると1本目は
    //   "connected" を出さないまま "disconnected" だけを出し、最終的な "connected" は
    //   1本で止まって下の until がタイムアウトする（ローカル実測で8回中2回、CI でも再現）。
    //   アサートする当のイベントそのものを待つのが正しい
    await until(() => events.includes("connected"));

    s.sockets[0].close(1013, "too slow");
    // ★ ここも同じ理由でサーバー側カウンタではなくイベントを待つ。
    //   `=== 2` ではなく `>= 2` にしているのは、until が10msポーリングのため
    //   単調増加するカウンタに `===` を使うと値を飛び越えたときに永久に成立しないから。
    //   ここでは connect() が pingWatchdogMs: 0 を渡しており、このテストにも2本目を
    //   閉じる操作が無いので3本目が生まれる経路は無く、到達不能な保険にすぎないが、
    //   万一起きたときは下の toHaveLength(2) まで到達させて「条件が満たされませんでした」
    //   ではなく実際の件数が見える差分で落とす。第2引数の 5000 も外している。
    //   core/vitest.config.ts は testTimeout を設定していないため、テストあたりの予算は
    //   vitest 既定の 5000ms で、ここまでの await が既にその一部を使っている。5000 を
    //   渡しても必ず vitest の予算に負けるので嘘の期限になる。既定の 3000 に戻せば
    //   backoffMinMs: 40 で再接続には十分間に合う
    await until(() => events.filter((e) => e === "connected").length >= 2);
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

  it("★ ハンドシェイクが 101 以外で返っても繋ぎ直す", async () => {
    // ws は emit("unexpected-response", …) が false を返したとき（＝リスナが1つも無いとき）
    // だけ abortHandshake を呼ぶ。ログを出すだけのリスナを張ると error も close も発火せず、
    // readyState が CONNECTING のまま固着して**再起動するまで永久に無音**になる。
    // 踏む条件: playerServerUrl が HTTP ポートを向いている / プロキシが 502 を返す / 401
    vi.spyOn(console, "error").mockImplementation(() => {});
    vi.spyOn(console, "warn").mockImplementation(() => {});

    let handshakes = 0;
    const denier = http.createServer((_req, res) => {
      handshakes++;
      res.writeHead(401);
      res.end();
    });
    httpServers.push(denier);
    await new Promise<void>((done) => denier.listen(0, "127.0.0.1", () => done()));
    const { port } = denier.address() as AddressInfo;

    const { events } = connect(`ws://127.0.0.1:${port}`);

    // 1回で終わらず、繋ぎ直して何度も叩きにいく
    await until(() => handshakes >= 3, 5000);
    expect(events).not.toContain("connected");
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
