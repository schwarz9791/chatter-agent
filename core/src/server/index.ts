#!/usr/bin/env node
/**
 * `chatter-agent-server` — `speech.jsonl` の新規行を WebSocket で配信する常駐プロセス。
 *
 * **判断ロジックを持たない**（docs/core.md）。差分を読んでそのまま流すだけ。
 * 何を喋るかは CLI が `speech.jsonl` に書いた時点で決まっている。
 *
 * 合成ルートなので、ここには配線と終了処理しか置かない。
 */

import * as chokidar from "chokidar";
import * as fs from "fs";
import * as path from "path";
import { createConfigStore } from "../core/config";
import { getSpeechLogPath } from "../core/paths";
import { createSpeechTail } from "./speechTail";
import { createWsServer } from "./wsServer";

const SHUTDOWN_STEP_TIMEOUT_MS = 1_500;
const SHUTDOWN_TIMEOUT_MS = 6_000;

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
  const logPath = getSpeechLogPath();
  console.log(`[Server] config: ${config.filePath}`);
  console.log(`[Server] speech log: ${logPath}`);

  // ★ 監視対象の親ディレクトリを先に作る。
  //   chokidar は存在しないファイルを watch すると親ディレクトリを watch しに行き、
  //   親も無ければさらに上へ遡ってしまう
  fs.mkdirSync(path.dirname(logPath), { recursive: true });

  const tail = createSpeechTail(logPath);
  // 起動前に溜まっていた分は流さない。取りこぼしを埋めたいクライアントは ?since= で要求する
  tail.seekToEnd();

  // ★ 監視より先にポートを押さえる。埋まっているなら監視を始める前に落ちるべき
  const wsServer = await createWsServer({
    host: config.get("host"),
    port: config.get("port"),
    backfill: (since) => tail.backfill(since),
  });

  const bound = wsServer.address();
  console.log(`[Server] listening on ws://${bound.host}:${bound.port}`);
  if (bound.host === "0.0.0.0") {
    console.warn("[Server] 0.0.0.0 は無認証で LAN 全体に露出します。信頼できない網では host を 127.0.0.1 に");
  }

  const flush = () => {
    for (const line of tail.readNew()) wsServer.broadcast(line);
  };

  const watcher = chokidar.watch(logPath, { ignoreInitial: true });
  watcher.on("add", flush);
  watcher.on("change", flush);
  watcher.on("unlink", flush); // ローテートで消えた直後の取りこぼしを拾う
  watcher.on("error", (err) => console.error("[Server] Watch error:", err));

  console.log("[Server] Ready");

  installShutdown(async () => {
    await step("file watcher", () => watcher.close());
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
