#!/usr/bin/env node
/**
 * `chatter-agent-speak` — hook から**毎 delta 起動される** CLI。
 *
 * バンドルして `plugin/bin/chatter-agent-speak.mjs` に出す（docs/core.md）。
 * npm 依存を持たないこと。ここから到達する範囲は Node 標準だけで閉じている。
 *
 * やることは短い:
 *   1. 無効化されていたら即終了
 *   2. ロックを取る。取れなければ `LOCK_MAX_WAIT_MS`（worker.ts）を使い切るまで待って試す
 *   3. spool をドレインする
 *   4. ロックを解放する
 *
 * **何があっても exit 0 で終える。** hook の失敗が Claude Code の表示を止めてはいけない。
 */

import { createConfigStore, isSpeakDisabled } from "../core/config";
import {
  getLockDir,
  getSpeechLogPath,
  getSpeechQueueDir,
  getSpeechStatePath,
  getSpoolDir,
  getSummarizerHomeDir,
  getSummarizerLogPath,
  getSummarizerSessionsPath,
  getWorkerStatePath,
} from "../core/paths";
import { createSpeechLog } from "../core/speechLog";
import { createSpeechQueue } from "../core/speechQueue";
import { RuleBasedEmotionClassifier } from "../emotion/ruleBasedEmotionClassifier";
import { createSummaryPipeline } from "../summarizer/summaryPipeline";
import { createPublisher } from "./publish";
import { acquireLockWithRetry, drainSpool } from "./worker";

function main(): void {
  // 無限ループ防止の第1層（設計書 §4-3）。要約プロセスはこれを付けて spawn される。
  // 環境変数は子プロセスの Claude Code とそのフックまで伝播する。
  // 判定は plugin/scripts/_lib.sh の chatter_disabled と同じ（→ core/config.ts）
  if (isSpeakDisabled()) return;

  const config = createConfigStore();

  const lock = acquireLockWithRetry(getLockDir());
  if (!lock) return; // 先行ワーカーが処理する

  try {
    const speechLog = createSpeechLog({
      logPath: getSpeechLogPath(),
      statePath: getSpeechStatePath(),
      maxBytes: config.get("speechLogMaxBytes"),
    });
    const speechQueue = createSpeechQueue(getSpeechQueueDir());
    // 落ちた enqueue が残した .tmp はロック下の1プロセスだけが掃除する。
    // server から掃除すると、CLI が今まさに書いている途中の tmp を消しうる
    speechQueue.sweepTmp();

    const classifier = new RuleBasedEmotionClassifier();

    // 要約 CLI 自身が起動したときは isSpeakDisabled() の早期 return で既にここへ到達しない
    // （無限ループ防止の第1層）。ここに来ることそのものが、その1層目が効いていることの証拠
    const summarize = createSummaryPipeline({
      // ★ config は mtime スタンプで再読込する作り。上の6つを値で渡すと起動時の1回きりの値が
      //   固定されてしまい設定変更が効かなくなるので、下の publish の maxEntries と同じ理由で
      //   getter で渡す
      isEnabled: () => config.get("aiSummaryEnabled"),
      getThreshold: () => config.get("aiSummaryThreshold"),
      getTimeoutMs: () => config.get("aiSummaryTimeoutMs"),
      getMaxPerDrain: () => config.get("aiSummaryMaxPerDrain"),
      getCommand: () => config.get("aiSummaryCommand"),
      getModel: () => config.get("aiSummaryModel"),
      homeDir: getSummarizerHomeDir(),
      logPath: getSummarizerLogPath(),
    });

    drainSpool({
      spoolDir: getSpoolDir(),
      // 記録と配信を同じロック下で書く。順序はここで確定する。
      // publish は append 後の enqueue/trim では throw しない（cli/publish.ts）
      publish: createPublisher({
        speechLog,
        speechQueue,
        // config は mtime スタンプで再読込する作りなので、ここも参照のたびに読み直す
        maxEntries: () => config.get("speechQueueMaxEntries"),
      }),
      workerStatePath: getWorkerStatePath(),
      summarizerSessionsPath: getSummarizerSessionsPath(),
      speakPrompts: config.get("speakPrompts"),
      spoolMaxAgeMs: config.get("spoolMaxAgeHours") * 60 * 60 * 1000,
      classify: (text) => classifier.classify(text),
      summarize,
    });
  } finally {
    lock.release();
  }
}

try {
  main();
} catch (err) {
  // hook 経路なので stderr は握り潰される可能性が高いが、手動実行時の手がかりに残す
  console.error("[chatter-agent-speak]", err);
}

process.exit(0);
