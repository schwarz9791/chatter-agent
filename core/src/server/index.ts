#!/usr/bin/env node
/**
 * `chatter-agent-server` — 配信キューを WebSocket で流す常駐プロセス。
 *
 * **判断ロジックを持たない**（docs/core.md）。キューを読んでそのまま流すだけ。
 * 何を喋るかは CLI が `speech/<seq>.json` を書いた時点で決まっている。
 *
 * 合成ルートなので、ここには配線と終了処理しか置かない。
 */

import * as fs from "fs";
import * as path from "path";
import { createConfigStore } from "../core/config";
import { getSpeechQueueDir } from "../core/paths";
import { createSpeechQueue } from "../core/speechQueue";
import { createWsServer } from "./wsServer";

const SHUTDOWN_STEP_TIMEOUT_MS = 1_500;
const SHUTDOWN_TIMEOUT_MS = 6_000;

/**
 * キューを見に行く間隔。
 *
 * ★ ファイル監視は使わない。単一ファイルの監視でローテートの取りこぼしを実測で踏んでおり、
 *   `readdir` の方が経路として単純で確実。キューは高々数百件なので、10回/秒でも誤差。
 */
const POLL_INTERVAL_MS = 100;

/**
 * 起動時に残す entry の新しさの境界。これより古い entry だけを捨てる。
 *
 * ★ CLI は delta ごとに起動されるので、1つのメッセージの文は複数回のドレインに
 *   分かれて publish される。無条件の全消しだと、たまたま直前のドレインで積まれた
 *   entry まで巻き添えにして、マスコットが段落の途中から喋り出す。
 *   「落ちている間に書かれたものは定義上すべて古い」を、時間の条件に絞る
 */
const STARTUP_KEEP_MS = 10_000;

/**
 * 終了処理の1ステップを制限時間つきで実行する。
 * まとめて1つの watchdog に任せると「諦めた」ことしか分からず、
 * どのリソースが閉じられなかったのか追えなくなるため、名指しで報告して先へ進む。
 */
async function step(label: string, work: () => Promise<unknown>): Promise<void> {
  const deadline = new Promise<void>((resolve) => setTimeout(resolve, SHUTDOWN_STEP_TIMEOUT_MS).unref());
  await Promise.race([
    work().then(
      () => undefined,
      (err: unknown) => console.error(`[Server] ${label} の終了処理に失敗:`, err),
    ),
    deadline.then(() =>
      console.warn(`[Server] ${label} の終了処理が ${SHUTDOWN_STEP_TIMEOUT_MS}ms で返りませんでした`),
    ),
  ]);
}

function installShutdown(cleanup: () => Promise<void>): void {
  let shuttingDown = false;

  const onSignal = (signal: NodeJS.Signals) => {
    if (shuttingDown) {
      process.exit(130); // 2回目は即座に
      return;
    }
    shuttingDown = true;
    console.log(`[Server] ${signal} を受信。終了します`);

    setTimeout(() => {
      console.warn("[Server] 終了処理が長引いたため強制終了します");
      process.exit(1);
    }, SHUTDOWN_TIMEOUT_MS).unref();

    void cleanup().then(() => process.exit(0));
  };

  process.on("SIGINT", onSignal);
  process.on("SIGTERM", onSignal);
  // 常駐プロセスなので1件の rejection で落とさない
  process.on("unhandledRejection", (err) => console.error("[Server] Unhandled rejection:", err));
}

async function main(): Promise<void> {
  const config = createConfigStore();
  const queueDir = getSpeechQueueDir();
  console.log(`[Server] config: ${config.filePath}`);
  console.log(`[Server] speech queue: ${queueDir}`);

  fs.mkdirSync(path.dirname(queueDir), { recursive: true });
  const queue = createSpeechQueue(queueDir);

  /** 配信済みの最大 seq。接続直後の追いつきをここまでに限る */
  let sentUpTo = 0;

  // ★ 監視より先にポートを押さえる。埋まっているなら何も触る前に落ちるべき
  const wsServer = await createWsServer({
    host: config.get("host"),
    port: config.get("port"),
    allowedOrigins: config.get("allowedOrigins"),

    onConnect: (send) => {
      // ★ sentUpTo までしか送らないこと。その先は直後の poll が broadcast するので、
      //   ここでも送ると新規クライアントだけが二重に受け取る
      let sent = 0;
      for (const seq of queue.list()) {
        if (seq > sentUpTo) break;
        const line = queue.read(seq);
        if (line === null) continue; // 扱いの整理は別タスク。ここでは飛ばすだけ
        if (!send(line)) break;
        sent++;
      }
      if (sent > 0) console.log(`[Server] 未読 ${sent} 件を送りました`);
    },

    onAck: (seq) => {
      const removed = queue.ackUpTo(seq);
      if (removed > 0) console.log(`[Server] seq<=${seq} を ${removed} 件消しました`);
    },
  });

  const bound = wsServer.address();
  console.log(`[Server] listening on ws://${bound.host}:${bound.port}`);
  if (bound.host === "0.0.0.0") {
    console.warn("[Server] 0.0.0.0 は無認証で LAN 全体に露出します。信頼できない網では host を 127.0.0.1 に");
  }

  // ★ wipe は bind の後。先に消すと、ポートが埋まって起動に失敗したときに
  //   走っている方のサーバーのキューを消してしまう。
  //   落ちている間に書かれたものは定義上すべて古いので、捨てて正しい
  const wiped = queue.dropOlderThan(STARTUP_KEEP_MS);
  if (wiped > 0) console.log(`[Server] 起動前に溜まっていた ${wiped} 件を捨てました`);

  const poll = setInterval(() => {
    for (const seq of queue.list()) {
      if (seq <= sentUpTo) continue;
      const line = queue.read(seq);
      if (line === null) continue; // 扱いの整理は別タスク。ここでは飛ばすだけ
      wsServer.broadcast(line);
      sentUpTo = seq;
    }
  }, POLL_INTERVAL_MS);
  poll.unref();

  console.log("[Server] Ready");

  installShutdown(async () => {
    clearInterval(poll);
    await step("websocket server", () => wsServer.close());
  });
}

main().catch((err: unknown) => {
  if ((err as NodeJS.ErrnoException)?.code === "EADDRINUSE") {
    console.error("[Server] ポートが使用中です。config.json の port か CHATTER_AGENT_PORT を変えてください");
  } else {
    console.error("[Server] 起動に失敗しました:", err);
  }
  process.exit(1);
});
