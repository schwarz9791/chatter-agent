#!/usr/bin/env node
/**
 * `chatter-agent-speak` — hook から**毎 delta 起動される** CLI。
 *
 * バンドルして `plugin/bin/chatter-agent-speak.mjs` に出す（docs/core.md）。
 * npm 依存を持たないこと。ここから到達する範囲は Node 標準だけで閉じている。
 *
 * やることは短い:
 *   1. 無効化されていたら即終了
 *   2. ロックを取る。取れなければ即終了（先行ワーカーが拾う）
 *   3. spool をドレインする
 *   4. ロックを解放する
 *
 * **何があっても exit 0 で終える。** hook の失敗が Claude Code の表示を止めてはいけない。
 */

import { createConfigStore } from "../core/config";
import { getLockDir, getSpeechLogPath, getSpeechStatePath, getSpoolDir, getWorkerStatePath } from "../core/paths";
import { createSpeechLog } from "../core/speechLog";
import { RuleBasedEmotionClassifier } from "../emotion/ruleBasedEmotionClassifier";
import { acquireLock } from "./lock";
import { drainSpool } from "./worker";

function main(): void {
  // 無限ループ防止の第1層（設計書 §4-3）。要約プロセスはこれを付けて spawn される。
  // 環境変数は子プロセスの Claude Code とそのフックまで伝播する
  if (process.env.CHATTER_AGENT_DISABLE) return;

  const config = createConfigStore();

  const lock = acquireLock(getLockDir());
  if (!lock) return; // 先行ワーカーが処理する

  try {
    const speechLog = createSpeechLog({
      logPath: getSpeechLogPath(),
      statePath: getSpeechStatePath(),
      maxBytes: config.get("speechLogMaxBytes"),
      generations: config.get("speechLogGenerations"),
    });

    const classifier = new RuleBasedEmotionClassifier();

    drainSpool({
      spoolDir: getSpoolDir(),
      speechLog,
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
