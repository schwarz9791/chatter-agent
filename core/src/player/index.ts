#!/usr/bin/env node
/**
 * `chatter-agent-player` — 配信された発話を音にする常駐プロセス。
 *
 * **判断ロジックを持たない**（docs/core.md）。何をいつ合成し、いつ鳴らし、いつ ack するかは
 * `playbackQueue.ts` に出してある。ここはコマンドを実行して結果をイベントとして戻すだけの
 * ドライバと、起動・終了の配線。
 *
 * 起動順は **ロック → 一時ディレクトリ → エンジン疎通 → 接続**。
 *
 * ★ エンジンに繋がるまで WebSocket を開かないこと。合成の失敗は「1回リトライして捨てて ack」
 *   なので、AivisSpeech を起動し忘れたまま player を先に立ち上げると、溜まっていたキューが
 *   数百 ms で全部捨てられる。繋いでいなければ発話は届かず、サーバー側のキューに残る。
 */

import { createConfigStore } from "../core/config";
import { acquireLock } from "../core/lock";
import { getPlayerLockDir, getPlayerTmpDir } from "../core/paths";
import { createAudioPlayer, playbackTimeoutMs } from "./audioPlayer";
import { createSpeechClient, deriveServerUrl } from "./client";
import { createDefaultOptions, createPlaybackState, reduce } from "./playbackQueue";
import type { PlaybackCommand, PlaybackEvent } from "./playbackQueue";
import { parseSpeechFrame } from "./speechFrame";
import { createVoicevoxClient, flattenStyles, hasStyle } from "./voicevoxClient";
import type { Speaker } from "./voicevoxClient";

/** `server/index.ts` と同じ形。どのリソースが閉じられなかったか追えるようにする */
const SHUTDOWN_STEP_TIMEOUT_MS = 2_500;
const SHUTDOWN_TIMEOUT_MS = 6_000;

/** 古さの判定と stall watchdog を進めるためだけの間隔。キューは見に行かない（配信は push） */
const TICK_INTERVAL_MS = 5_000;

/** エンジンの起動を待つ間隔。モデルのロードで数十秒かかることがある */
const ENGINE_RETRY_MIN_MS = 1_000;
const ENGINE_RETRY_MAX_MS = 15_000;

/** 話者が見つからないときに案内する候補の数 */
const SPEAKER_HINT_LIMIT = 5;

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

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
  console.log(`[Player] engine: ${config.get("ttsBaseUrl")} (speaker=${config.get("ttsSpeakerId")})`);

  const tts = createVoicevoxClient({
    baseUrl: config.get("ttsBaseUrl"),
    speakerId: config.get("ttsSpeakerId"),
    timeoutMs: config.get("synthesisTimeoutMs"),
  });

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

  let stopping = false;
  await waitForEngine(tts, config.get("ttsSpeakerId"), () => stopping);
  if (stopping) return;

  /** seq ごとの再生タイムアウト。合成した WAV の実長から決まる */
  const playbackTimeouts = new Map<number, number>();

  const client = createSpeechClient({
    url,
    onFrame: (raw) => {
      const record = parseSpeechFrame(raw);
      if (!record) {
        // 知らない形は捨てる。接続は切らない（server の parseAck と対称）
        console.warn("[Player] 読めないフレームを捨てました");
        return;
      }
      dispatch({ kind: "received", record });
    },
    onConnected: () => dispatch({ kind: "connected" }),
    onDisconnected: () => dispatch({ kind: "disconnected" }),
  });

  function dispatch(event: PlaybackEvent): void {
    const { commands } = reduce(state, event, Date.now());
    for (const command of commands) execute(command);
  }

  function execute(command: PlaybackCommand): void {
    switch (command.kind) {
      case "synthesize":
        void synthesize(command.seq, command.text);
        break;
      case "play":
        void play(command.seq, command.file);
        break;
      case "ack":
        client.ack(command.seq);
        break;
      case "discardFile":
        playbackTimeouts.delete(command.seq);
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

  async function synthesize(seq: number, text: string): Promise<void> {
    try {
      const wav = await tts.synthesize(text);
      // ★ 再生のタイムアウトは WAV の実長から決める。固定値だと長文が切れるか、
      //   ハングを見逃すかのどちらかになる
      playbackTimeouts.set(seq, playbackTimeoutMs(wav));
      const file = audio.write(seq, wav);
      dispatch({ kind: "synthesized", seq, file });
    } catch (err) {
      dispatch({ kind: "synthesisFailed", seq, reason: err instanceof Error ? err.message : String(err) });
    }
  }

  async function play(seq: number, file: string): Promise<void> {
    // 合成を経ずにここへ来ることは無いが、取り違えたときに固定値へ倒れる方が安全
    const timeout = playbackTimeouts.get(seq) ?? playbackTimeoutMs(new ArrayBuffer(0));
    try {
      await audio.play(file, timeout);
      dispatch({ kind: "played", seq });
    } catch (err) {
      dispatch({ kind: "playbackFailed", seq, reason: err instanceof Error ? err.message : String(err) });
    }
  }

  client.start();

  // ★ unref しないこと。player は常駐プロセスだが、server と違って listen しているものが無い。
  //   接続が切れている間はこれが唯一の生存理由になり、unref すると黙って終了する
  //   （`server/index.ts` の poll が unref できるのは WebSocketServer が参照を持つため）
  const tick = setInterval(() => dispatch({ kind: "tick" }), TICK_INTERVAL_MS);

  console.log("[Player] Ready");

  installShutdown(async () => {
    stopping = true;
    clearInterval(tick);
    await step("websocket client", () => client.close());
    // 親が exit しても afplay は死なない。プロセスが消えた後も音が鳴り続ける
    audio.stopAll();
    audio.cleanup();
    lock.release();
  });
}

/**
 * エンジンに繋がるまで待ち、話者 ID の存在を確かめる。
 *
 * ★ ここで待つのが「起動し忘れでバックログを全部捨てる」事故への唯一の歯止め。
 * ★ 話者 ID の不一致は `/audio_query` の 4xx になり、全文が捨てられて**無音**になる。
 *   症状から設定ミスに辿り着けないので、起動時に候補を並べる。
 */
async function waitForEngine(
  tts: ReturnType<typeof createVoicevoxClient>,
  speakerId: number,
  stopping: () => boolean,
): Promise<void> {
  let delay = ENGINE_RETRY_MIN_MS;
  let warned = false;
  let speakers: Speaker[] | null = null;

  while (!stopping()) {
    try {
      speakers = await tts.listSpeakers();
      break;
    } catch (err) {
      if (!warned) {
        warned = true;
        console.warn(`[Player] 音声合成エンジンに繋がりません (${tts.baseUrl}): ${String(err)}`);
        console.warn("[Player] 繋がるまで待ちます。AivisSpeech を起動してください");
      }
      await sleep(delay);
      delay = Math.min(delay * 2, ENGINE_RETRY_MAX_MS);
    }
  }

  if (!speakers) return;
  if (warned) console.log("[Player] 音声合成エンジンに繋がりました");

  if (hasStyle(speakers, speakerId)) return;

  console.warn(`[Player] 話者 ID ${speakerId} がこのエンジンにありません。合成は失敗し、無音になります`);
  const hints = flattenStyles(speakers).slice(0, SPEAKER_HINT_LIMIT);
  for (const hint of hints) console.warn(`[Player]   ${hint.id}  ${hint.label}`);
  console.warn("[Player] config.json の ttsSpeakerId か CHATTER_AGENT_TTS_SPEAKER_ID で指定してください");
}

main().catch((err: unknown) => {
  console.error("[Player] 起動に失敗しました:", err);
  process.exit(1);
});
