#!/usr/bin/env node
/**
 * `chatter-agent-player` — 配信された発話を音にする常駐プロセス。
 *
 * **判断ロジックを持たない**（docs/core.md）。何をいつ取りに行き、いつ鳴らし、いつ ack するかは
 * `playbackQueue.ts` に出してある。ここはコマンドを実行して結果をイベントとして戻すだけの
 * ドライバと、起動・終了の配線。
 *
 * 起動順は **ロック → 一時ディレクトリ → 接続**。
 *
 * ★ #29 で**合成がサーバーへ移った**。`audio_query` → `synthesis` の2往復は
 *   `GET /audio/<epoch>-<seq>.wav` の1往復になり、エンジンの場所を知る必要が無くなった。
 *
 * ★ 以前ここには「エンジンに繋がるまで WebSocket を開かない」という歯止めがあった。
 *   起動し忘れたまま繋ぐと、失敗が「1回リトライして捨てて ack」に流れて**溜まっていた
 *   キューが数百 ms で全部捨てられる**ためだった。その役目は
 *   **503（あとで取りに来い）を試行回数に数えない**ことへ移してある
 *   （→ `audioFetcher.ts` / `playbackQueue.ts` の `audioUnavailable`）。
 */

import { createConfigStore } from "../core/config";
import { acquireLock } from "../core/lock";
import { getPlayerLockDir, getPlayerTmpDir } from "../core/paths";
import { createAudioFetcher, deriveAudioBaseUrl } from "./audioFetcher";
import { createAudioPlayer, playbackTimeoutMs } from "./audioPlayer";
import { createSpeechClient, deriveServerUrl } from "./client";
import type { SpeechClient } from "./client";
import { createDefaultOptions, createPlaybackState, reduce } from "./playbackQueue";
import type { PlaybackCommand, PlaybackEvent } from "./playbackQueue";
import { isAudioUndeclared, parseSpeechFrame } from "./speechFrame";

/**
 * 終了処理の1ステップの制限時間。`server/index.ts` と同じ形で、どのリソースが閉じられなかったかを
 * 名指しで報告するために分けてある。
 *
 * ★ `client.close()` が持つ terminate() 救済の猶予（1秒）より大きくすること。ここが猶予以下だと、
 *   `step()` の watchdog が先に諦めて次へ進み、応答しないソケットを terminate する経路に
 *   到達できない。`SHUTDOWN_TIMEOUT_MS`（全体の上限）は超えないこと。
 */
const SHUTDOWN_STEP_TIMEOUT_MS = 2_500;
const SHUTDOWN_TIMEOUT_MS = 6_000;

/**
 * 古さの判定・stall watchdog・**503 のバックオフ**を進めるための間隔。
 * キューは見に行かない（配信は push）。
 *
 * ★ **`playbackQueue` の `audioRetryMs`（既定1秒）と揃えること。** 503 からの取り直しは
 *   「バックオフが明けた後の最初の tick」で起きるので、ここが長いとその分だけ復帰が遅れる。
 *   以前 5 秒だったのは、進める判断が古さの判定と stall watchdog しか無かったため。
 *   1回の tick は小さな reducer を1周するだけなので、頻度を上げても誤差。
 */
const TICK_INTERVAL_MS = 1_000;

async function step(label: string, work: () => Promise<unknown>): Promise<void> {
  const deadline = new Promise<void>((resolve) => setTimeout(resolve, SHUTDOWN_STEP_TIMEOUT_MS).unref());
  await Promise.race([
    work().then(
      () => undefined,
      (err: unknown) => console.error(`[Player] ${label} の終了処理に失敗:`, err),
    ),
    deadline.then(() =>
      console.warn(`[Player] ${label} の終了処理が ${SHUTDOWN_STEP_TIMEOUT_MS}ms で返りませんでした`),
    ),
  ]);
}

function installShutdown(cleanup: () => Promise<void>): void {
  let shuttingDown = false;

  const onSignal = (signal: NodeJS.Signals) => {
    if (shuttingDown) {
      process.exit(130);
      return;
    }
    shuttingDown = true;
    console.log(`[Player] ${signal} を受信。終了します`);

    setTimeout(() => {
      console.warn("[Player] 終了処理が長引いたため強制終了します");
      process.exit(1);
    }, SHUTDOWN_TIMEOUT_MS).unref();

    void cleanup().then(() => process.exit(0));
  };

  process.on("SIGINT", onSignal);
  process.on("SIGTERM", onSignal);
  // 常駐プロセスなので1件の rejection / 例外で落とさない
  process.on("unhandledRejection", (err) => console.error("[Player] Unhandled rejection:", err));
  process.on("uncaughtException", (err) => console.error("[Player] Uncaught exception:", err));
}

async function main(): Promise<void> {
  // ★ ロックは接続より前に取ること。ack は累積で、server の `ackUpTo` は seq<=N を
  //   ファイル名で範囲削除する。2台目の速い ack が、1台目のまだ喋っていない entry を消す
  const lock = acquireLock(getPlayerLockDir());
  if (!lock) {
    console.error("[Player] 既に別の chatter-agent-player が動いています");
    process.exit(1);
  }

  const config = createConfigStore();
  const url = config.get("playerServerUrl") || deriveServerUrl(config.get("host"), config.get("port"));
  const tmpDir = getPlayerTmpDir();

  console.log(`[Player] config: ${config.filePath}`);
  console.log(`[Player] server: ${url}`);

  // 音声は WebSocket と同じ authority から取る。サーバーは自分の到達アドレスを
  // 知らないので、フレームには相対パスしか載らない（→ core/audioPath.ts）
  const audioFetcher = createAudioFetcher({
    baseUrl: deriveAudioBaseUrl(url),
    timeoutMs: config.get("audioFetchTimeoutMs"),
  });
  console.log(`[Player] audio: ${audioFetcher.baseUrl}/audio/`);

  const audio = createAudioPlayer({
    tmpDir,
    command: config.get("playerCommand"),
    args: config.get("playerArgs"),
  });
  // 前回の残骸を消す。ロックを取ってあるので、他プロセスの現物を消す心配は無い
  audio.reset();

  const state = createPlaybackState({
    ...createDefaultOptions(),
    lookahead: config.get("synthesisLookahead"),
    maxAgeMs: config.get("speechMaxAgeMs"),
    // サーバーは自分のキューにある分しか再送できないので、その上限を覚えていれば足りる
    seenCapacity: Math.max(config.get("speechQueueMaxEntries"), 512),
  });

  let client: SpeechClient | undefined;
  let tick: NodeJS.Timeout | undefined;

  // ★ 接続より前に登録すること。ロックと一時ディレクトリを取った後に落ちると、
  //   `player.lock/` と `player-tmp/` が残る
  installShutdown(async () => {
    if (tick) clearInterval(tick);
    const socket = client;
    if (socket) await step("websocket client", () => socket.close());
    // 親が exit しても afplay は死なない。プロセスが消えた後も音が鳴り続ける
    audio.stopAll();
    audio.cleanup();
    lock.release();
  });

  /**
   * 再生タイムアウト。合成した WAV の実長から決まる。
   * キーは `${epoch}:${seq}` — `seq` は採番のやり直しを跨いで一意でない
   */
  const playbackTimeouts = new Map<string, number>();
  const timeoutKey = (epoch: number, seq: number) => `${epoch}:${seq}`;

  /**
   * 接続ごとに1回で足りる警告のラッチ。`onConnected` で戻す。
   *
   * ★ 読めないフレームは**壊れたプロデューサーが同じ形を送り続ける**ので、毎フレーム出すと
   *   ログが洪水になる（`server/wsServer.ts` の `warnedBadAck` と対称）。
   * ★ `audio` キーの有無は**接続ごとに最初のフレームだけ**見れば足りる。サーバーが接続の
   *   途中でフレームの形を変えることは無く、`onConnected` は `open` ハンドラから呼ばれるので
   *   必ずフレームより先に来る（`client.ts`）。
   */
  let warnedBadFrame = false;
  let audioDeclarationChecked = false;

  client = createSpeechClient({
    url,
    onFrame: (raw) => {
      const record = parseSpeechFrame(raw);
      if (!record) {
        // 知らない形は捨てる。接続は切らない（server の parseAck と対称）
        if (!warnedBadFrame) {
          warnedBadFrame = true;
          console.warn("[Player] 読めないフレームを捨てました");
        }
        return;
      }
      // ★ 読めたフレームで判定すること。最初のフレームが読めなかったら次で見る
      if (!audioDeclarationChecked) {
        audioDeclarationChecked = true;
        if (isAudioUndeclared(raw)) {
          console.warn("[Player] サーバーのフレームに audio がありません（#29 より前のサーバー？）");
          console.warn("[Player] 音声は鳴らず、すべての発話が無音のまま ack されます");
        }
      }
      dispatch({ kind: "received", record });
    },
    onConnected: () => {
      warnedBadFrame = false;
      audioDeclarationChecked = false;
      dispatch({ kind: "connected" });
    },
    onDisconnected: () => dispatch({ kind: "disconnected" }),
  });

  function dispatch(event: PlaybackEvent): void {
    const commands = reduce(state, event, Date.now());
    for (const command of commands) execute(command);
  }

  function execute(command: PlaybackCommand): void {
    switch (command.kind) {
      case "fetchAudio":
        void fetchAudio(command.epoch, command.seq, command.path);
        break;
      case "play":
        void play(command.epoch, command.seq, command.file);
        break;
      case "ack":
        // client の生成前にコマンドが出ることは無いが、`installShutdown` を先に登録した都合で
        // 型の上では undefined になりうる
        client?.ack(command.seq, command.epochId);
        break;
      case "dropPendingAck":
        client?.dropPendingAck();
        break;
      case "discardFile":
        playbackTimeouts.delete(timeoutKey(command.epoch, command.seq));
        audio.discard(command.file);
        break;
      case "log":
        console.log(`[Player] ${command.message}`);
        break;
      case "warn":
        console.warn(`[Player] ${command.message}`);
        break;
    }
  }

  async function fetchAudio(epoch: number, seq: number, audioPath: string): Promise<void> {
    let result;
    try {
      result = await audioFetcher.fetchAudio(audioPath);
    } catch (err) {
      // fetchAudio は自分で握るので通常ここには来ない。来たら試行回数を消費する側に倒す
      dispatch({ kind: "audioFailed", epoch, seq, reason: err instanceof Error ? err.message : String(err) });
      return;
    }

    // ★ 503 と 404 を「失敗」に混ぜないこと。混ぜると `synthesisAttempts` が数 ms で
    //   燃え尽き、エンジンが落ちているだけでバックログが全部捨てられる
    if (result.kind === "unavailable") {
      dispatch({ kind: "audioUnavailable", epoch, seq, reason: result.reason });
      return;
    }
    if (result.kind === "gone") {
      dispatch({ kind: "audioGone", epoch, seq, reason: result.reason });
      return;
    }
    if (result.kind === "failed") {
      dispatch({ kind: "audioFailed", epoch, seq, reason: result.reason });
      return;
    }

    try {
      // ★ WAV を書き終えてから Map に入れること。`write` が投げる（ENOSPC、終了処理で
      //   一時dir が消える）と item は `file === null` で done に落ち、`discardFile` が
      //   出ないので、先に set していたエントリが**永久に残る**
      const file = audio.write(epoch, seq, result.wav);
      // ★ 再生のタイムアウトは WAV の実長から決める。固定値だと長文が切れるか、
      //   ハングを見逃すかのどちらかになる
      playbackTimeouts.set(timeoutKey(epoch, seq), playbackTimeoutMs(result.wav));
      dispatch({ kind: "audioReady", epoch, seq, file });
    } catch (err) {
      dispatch({ kind: "audioFailed", epoch, seq, reason: err instanceof Error ? err.message : String(err) });
    }
  }

  async function play(epoch: number, seq: number, file: string): Promise<void> {
    // 合成を経ずにここへ来ることは無いが、取り違えたときに固定値へ倒れる方が安全
    const timeout = playbackTimeouts.get(timeoutKey(epoch, seq)) ?? playbackTimeoutMs(new ArrayBuffer(0));
    try {
      await audio.play(file, timeout);
      dispatch({ kind: "played", epoch, seq });
    } catch (err) {
      dispatch({ kind: "playbackFailed", epoch, seq, reason: err instanceof Error ? err.message : String(err) });
    }
  }

  client.start();

  // ★ unref しないこと。player は常駐プロセスだが、server と違って listen しているものが無い。
  //   接続が切れている間はこれが唯一の生存理由になり、unref すると黙って終了する
  //   （`server/index.ts` の poll が unref できるのは WebSocketServer が参照を持つため）
  tick = setInterval(() => dispatch({ kind: "tick" }), TICK_INTERVAL_MS);

  console.log("[Player] Ready");
}

main().catch((err: unknown) => {
  console.error("[Player] 起動に失敗しました:", err);
  process.exit(1);
});
