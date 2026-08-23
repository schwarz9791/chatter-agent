#!/usr/bin/env node
/**
 * `chatter-agent-server` — 配信キューを WebSocket で流し、音声を HTTP で配る常駐プロセス。
 *
 * **判断ロジックはここに置かない**（docs/core.md）。何を配信済みとするか・ack をどう
 * クランプするかは `dispatcher.ts`、音声を作って持つかは `audioStore.ts` に出してある。
 * ここには配線と終了処理しか置かない。
 *
 * 起動順は **ロック → bind → 古いキューの掃除 → poll**。ロックが最初なのは、
 * bind より後ろだと「2台目が別ポートで bind に成功し、1台目の未配信キューを
 * 巻き込む」窓が残るため（F 参照）。
 *
 * ★ **WebSocket と HTTP は同じポート。** `http.Server` を先に作り、`ws` を載せてから
 *   listen する（順序の理由は `wsServer.ts`）。listen そのものは `createWsServer` が握る。
 */

import * as fs from "fs";
import * as path from "path";
import { createConfigStore } from "../core/config";
import { acquireLock } from "../core/lock";
import { getServerLockDir, getSpeechQueueDir } from "../core/paths";
import { createSpeechQueue } from "../core/speechQueue";
import { createVoicevoxClient, flattenStyles, hasStyle } from "../tts/voicevoxClient";
import { createAudioStore } from "./audioStore";
import { createDispatcher, type Dispatcher } from "./dispatcher";
import { createAudioHttpServer } from "./httpServer";
import { createWsServer } from "./wsServer";

/**
 * 終了処理の1ステップの制限時間。
 *
 * ★ `CLOSE_GRACE_MS`（wsServer.close() が持つ terminate() 救済の猶予）より大きくすること。
 *   ここが `CLOSE_GRACE_MS` 以下だと、`step()` の watchdog が先に諦めて次へ進んでしまい、
 *   wsServer 側の救済（応答しないクライアントを terminate する経路）に到達できない。
 *   `SHUTDOWN_TIMEOUT_MS`（全体の上限）は超えないこと。
 */
const SHUTDOWN_STEP_TIMEOUT_MS = 2_500;
const SHUTDOWN_TIMEOUT_MS = 6_000;

/**
 * キューを見に行く間隔。
 *
 * ★ ファイル監視は使わない。単一ファイルの監視でローテートの取りこぼしを実測で踏んでおり、
 *   `readdir` の方が経路として単純で確実。
 *
 * ★ 50ms（20回/秒）にできるのは、`poll()` が呼ぶ `queue.list()` がファイル名の列挙だけで
 *   中身を読まないため。`list()` / `read()` に分ける前の `readAll()`（毎回キュー全件を
 *   readFileSync）は実測 7.3〜9.6ms/回（500件）かかっていたが、列挙だけなら 0.33ms/回に
 *   落ちる。20回/秒 × 0.33ms はコアの 0.7% 程度で、頻度を上げても誤差の範囲に収まる。
 */
const POLL_INTERVAL_MS = 50;

/**
 * 起動時に残す entry の新しさの境界。これより古い entry だけを捨てる。
 *
 * ★ [#30] でこの根拠は成立しなくなっている。以前は「CLI は delta ごとに起動されるので、
 *   1つのメッセージの文は複数回のドレインに分かれて publish される。無条件の全消しだと、
 *   たまたま直前のドレインで積まれた entry まで巻き添えにして、マスコットが段落の途中から
 *   喋り出す」という理由でこの時間条件を置いていた。今は `final` を待ってメッセージ全文を
 *   1回の enqueue でまとめて publish するので、途中から喋り出すことはそもそも起こらず、
 *   **メッセージを丸ごと残すか丸ごと捨てるかにしかならない**。
 *
 *   実際の挙動: 例えば30文のメッセージがサーバー再起動の11秒前に enqueue されていた場合、
 *   既定の `STARTUP_KEEP_MS = 10_000` では境界を超えているので丸ごと捨てられ、
 *   クライアントには1文も届かない。逆に9秒前なら丸ごと残る。
 *
 *   **消えること自体は方針として許容している**（10秒以上前に止まっていた発話を、今さら
 *   読み上げても体験として噛み合わない）。ここに残っているのは秒数の閾値であって、
 *   「途中から喋り出すのを防ぐ」という導出根拠はもう無い。
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
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
  // 常駐プロセスなので1件の rejection / 例外で落とさない
  process.on("unhandledRejection", (err) => console.error("[Server] Unhandled rejection:", err));
  process.on("uncaughtException", (err) => console.error("[Server] Uncaught exception:", err));
}

async function main(): Promise<void> {
  // ★ ロックは bind より前に取ること。配信キューのパス（{root}/speech）にはポートも
  //   インスタンス識別子も入っていないので、2台目が別ポートで bind に成功すると、
  //   起動時の掃除（下の dropOlderThan）が1台目の未配信キューを消し、定常状態でも
  //   両者が独立に配信して二重再生になる。「wipe は bind の後だから安全」という順序の
  //   工夫では、同じポートでの衝突にしか効かない。キューを所有できるサーバーを1台に絞る
  const serverLock = acquireLock(getServerLockDir());
  if (!serverLock) {
    console.error("[Server] 既に別の chatter-agent-server が動いています");
    process.exit(1);
  }

  const config = createConfigStore();
  const queueDir = getSpeechQueueDir();
  console.log(`[Server] config: ${config.filePath}`);
  console.log(`[Server] speech queue: ${queueDir}`);

  fs.mkdirSync(path.dirname(queueDir), { recursive: true });
  const queue = createSpeechQueue(queueDir);

  // ★ 音声はプロセス内にしか持たない（`audioStore.ts`）。合成は GET が来たときに走るので、
  //   誰も繋いでいない間はエンジンを一度も叩かない
  //
  // ★ **合成のたびに現在の config から組む。** config は参照のたびに mtime スタンプを見て
  //   読み直す作りなので、起動時の1回きりで固定すると `ttsSpeakerId` を直しても
  //   サーバーを再起動するまで効かない。無音の原因として真っ先に疑ってほしい値なので、
  //   直したらすぐ効く方がよい（クライアント側の警告もそこを名指しする）。
  //   クライアントの生成は object literal と closure だけなので、GET のたびに作って問題ない
  const currentTts = () =>
    createVoicevoxClient({
      baseUrl: config.get("ttsBaseUrl"),
      speakerId: config.get("ttsSpeakerId"),
      timeoutMs: config.get("synthesisTimeoutMs"),
    });
  const audioStore = createAudioStore({ synthesize: (text) => currentTts().synthesize(text) });

  const httpServer = createAudioHttpServer({
    store: audioStore,
    // ★ 本文の権威はキュー。ack / trim で消えた entry の音声は作らない
    lookup: (seq) => queue.read(seq),
    allowedOrigins: config.get("allowedOrigins"),
    disabled: () => !config.get("ttsEnabled"),
  });

  // wsServer の onConnect / onAck から参照するが、生成は wsServer の後（下記）。
  // どちらのコールバックも実際の接続・メッセージが来るまで呼ばれないので、
  // bind が終わってから dispatcher を作っても間に合う
  let dispatcher: Dispatcher;

  // ★ 監視より先にポートを押さえる。埋まっているなら何も触る前に落ちるべき
  const wsServer = await createWsServer({
    host: config.get("host"),
    port: config.get("port"),
    server: httpServer,
    allowedOrigins: config.get("allowedOrigins"),
    onConnect: (send) => dispatcher.catchUp(send),
    onAck: (seq, epoch) => dispatcher.ack(seq, epoch),
  });

  dispatcher = createDispatcher({
    queue,
    broadcast: (line) => wsServer.broadcast(line),
    audioEnabled: () => config.get("ttsEnabled"),
  });

  const bound = wsServer.address();
  console.log(`[Server] listening on ws://${bound.host}:${bound.port}`);
  if (config.get("ttsEnabled")) {
    console.log(`[Server] audio: http://${bound.host}:${bound.port}/audio/ (engine: ${config.get("ttsBaseUrl")})`);
  } else {
    console.log("[Server] ttsEnabled=false: 音声は配りません（クライアントは無音で ack します）");
  }
  if (bound.host === "0.0.0.0") {
    console.warn("[Server] 0.0.0.0 は無認証で LAN 全体に露出します。信頼できない網では host を 127.0.0.1 に");
  }

  // ★ サーバーは1台しかいない前提（上のロック）なので、「2台目が1台目のキューを消す」
  //   事故はここでは考えなくてよい。掃除は STARTUP_KEEP_MS の時間条件だけで判断する
  //   （全消し clear() ではない。落ちている間に書かれた古い entry だけを捨てる。
  //   理由は STARTUP_KEEP_MS のコメント参照）
  const wiped = queue.dropOlderThan(STARTUP_KEEP_MS);
  if (wiped > 0) console.log(`[Server] 起動前に溜まっていた ${wiped} 件を捨てました`);

  // ★ poll を握ること。1接続の送信で throw すると、そのポールの残り entry が
  //   `delivered` に入らないまま抜け、**次のポールで他のクライアントへ二重送信される**。
  //   常駐プロセスの `uncaughtException` ガードはプロセスを守るだけで、ここは守らない
  const poll = setInterval(() => {
    try {
      dispatcher.poll();
    } catch (err) {
      console.error("[Server] ポーリングに失敗しました:", err);
    }
  }, POLL_INTERVAL_MS);
  poll.unref();

  console.log("[Server] Ready");

  // ★ **エンジンの疎通で起動を止めないこと。** テキストの配信は音声と独立している。
  //   止めると、エンジンを起動し忘れているだけで発話そのものが1文も届かなくなり、
  //   クライアント側からは「数十秒の無音は正常」と区別できない（docs/protocol.md）。
  //   合成が要るタイミングで 503 を返す方が、原因が症状に出る。
  //
  // ★ 話者 ID の不一致は `/audio_query` の 4xx になり、全文が 503 になって**無音**になる。
  //   症状から設定ミスに辿り着けないので、起動時に候補を並べておく。
  if (config.get("ttsEnabled")) void checkEngine(currentTts(), config.get("ttsSpeakerId"));

  installShutdown(async () => {
    clearInterval(poll);
    // wsServer.close() は httpServer も閉じる（ws は options.server を閉じない → wsServer.ts）
    await step("websocket / http server", () => wsServer.close());
    // ★ isStale() は所有印が読めるなら pid の生死だけで判定する（core/lock.ts）。
    //   このサーバーは常駐で staleMs（既定60秒）をとうに超えて動き続けるが、
    //   pid が生きている限り自分のロックを他プロセスに奪われることはない。
    //   ここで release() し損ねてクラッシュしても、pid は死んでいるので
    //   次回起動時の isStale() が即座に回収する
    serverLock.release();
  });
}

/**
 * エンジンに繋がるか、話者 ID が実在するかを1回だけ見る。**待たないし、止めない。**
 * 結果はログに残すだけで、判断は `GET /audio/…` のたびに行われる。
 */
async function checkEngine(tts: ReturnType<typeof createVoicevoxClient>, speakerId: number): Promise<void> {
  let speakers;
  try {
    speakers = await tts.listSpeakers();
  } catch (err) {
    console.warn(`[Server] 音声合成エンジンに繋がりません (${tts.baseUrl}): ${String(err)}`);
    console.warn("[Server] 音声の GET は 503 を返します。テキストの配信は続きます");
    return;
  }

  if (hasStyle(speakers, speakerId)) return;

  console.warn(`[Server] ttsSpeakerId=${speakerId} はこのエンジンに存在しません。音声は 503 になります`);
  for (const style of flattenStyles(speakers).slice(0, 20)) {
    console.warn(`[Server]   ${style.id}  ${style.label}`);
  }
}

main().catch((err: unknown) => {
  if ((err as NodeJS.ErrnoException)?.code === "EADDRINUSE") {
    console.error("[Server] ポートが使用中です。config.json の port か CHATTER_AGENT_PORT を変えてください");
  } else {
    console.error("[Server] 起動に失敗しました:", err);
  }
  process.exit(1);
});
