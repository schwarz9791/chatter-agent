#!/usr/bin/env node
/**
 * `chatter-agent-speak` — hook から**毎 delta 起動される** CLI。
 *
 * バンドルして `plugin/bin/chatter-agent-speak.mjs` に出す（docs/core.md）。
 * npm 依存を持たないこと。ここから到達する範囲は Node 標準だけで閉じている。
 *
 * やることは短い:
 *   1. 無効化されていたら即終了
 *   2. ロックを取る。取れなければ少し待って数回だけ試す
 *   3. spool をドレインする
 *   4. ロックを解放する
 *
 * **何があっても exit 0 で終える。** hook の失敗が Claude Code の表示を止めてはいけない。
 */

import { createConfigStore } from "../core/config";
import {
  getLockDir,
  getSpeechLogPath,
  getSpeechQueueDir,
  getSpeechStatePath,
  getSpoolDir,
  getWorkerStatePath,
} from "../core/paths";
import { createSpeechLog } from "../core/speechLog";
import { createSpeechQueue } from "../core/speechQueue";
import { RuleBasedEmotionClassifier } from "../emotion/ruleBasedEmotionClassifier";
import { acquireLock, type Lock } from "../core/lock";
import { createPublisher } from "./publish";
import { drainSpool } from "./worker";

const LOCK_RETRIES = 3;
const LOCK_RETRY_DELAY_MS = 120;

/** 同期で待つ。デタッチ済みのプロセスなので、待っても hook はブロックしない */
function sleepSync(ms: number): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

/**
 * ロックを取る。取れなければ少し待って試し直す。
 *
 * ★ 一度で諦めてはいけない。先行ワーカーが最後の走査を終えてから解放するまでの窓に
 *   届いた spool は、そのワーカーにも拾われず、こちらが即終了すると誰にも拾われない。
 *   特に `permission_prompt` の Notification は**そのターン最後の hook イベント**なので、
 *   次のドレインを促すものが来ず、ユーザーが答えるまで通知が出ないままになる。
 */
function acquireLockWithRetry(lockDir: string): Lock | null {
  for (let attempt = 0; ; attempt++) {
    const lock = acquireLock(lockDir);
    if (lock) return lock;
    if (attempt >= LOCK_RETRIES) return null;
    sleepSync(LOCK_RETRY_DELAY_MS);
  }
}

function main(): void {
  // 無限ループ防止の第1層（設計書 §4-3）。要約プロセスはこれを付けて spawn される。
  // 環境変数は子プロセスの Claude Code とそのフックまで伝播する
  if (process.env.CHATTER_AGENT_DISABLE) return;

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
      speakPrompts: config.get("speakPrompts"),
      spoolMaxAgeMs: config.get("spoolMaxAgeHours") * 60 * 60 * 1000,
      classify: (text) => classifier.classify(text),
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
