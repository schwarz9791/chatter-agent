/**
 * ワーカーが**プロセスを跨いで**持ち回る状態。
 *
 * 上流 cc-mascot の `promptEventMonitor` は常駐プロセスなので、応答待ち通知の重複抑制を
 * クロージャの変数で持てた。chatter-agent の CLI は**毎 delta 起動して終了する**ので、
 * 同じ抑制を成立させるにはディスクに置くしかない。
 *
 * 書き込むのはロック保持者だけなので競合しない。
 */

import * as fs from "fs";
import { writeFileAtomic } from "../core/atomicWrite";

export interface WorkerState {
  /** 直前に読み上げた PreToolUse の prompt_id。付随する Notification を1回だけ捨てるために使う */
  pairedPromptId: string | null;
  pairedPromptAt: number;
  /** 直前に読み上げた応答待ちのテキスト。連投を抑制する */
  lastText: string;
  lastTextAt: number;
}

export function emptyWorkerState(): WorkerState {
  return { pairedPromptId: null, pairedPromptAt: 0, lastText: "", lastTextAt: 0 };
}

export function readWorkerState(statePath: string): WorkerState {
  try {
    const parsed: unknown = JSON.parse(fs.readFileSync(statePath, "utf-8"));
    if (typeof parsed === "object" && parsed !== null) {
      const record = parsed as Partial<WorkerState>;
      return {
        pairedPromptId: typeof record.pairedPromptId === "string" ? record.pairedPromptId : null,
        pairedPromptAt: typeof record.pairedPromptAt === "number" ? record.pairedPromptAt : 0,
        lastText: typeof record.lastText === "string" ? record.lastText : "",
        lastTextAt: typeof record.lastTextAt === "number" ? record.lastTextAt : 0,
      };
    }
  } catch {
    // 無い・壊れているのは異常ではない。抑制が1回効かないだけ
  }
  return emptyWorkerState();
}

export function writeWorkerState(statePath: string, state: WorkerState): void {
  writeFileAtomic(statePath, `${JSON.stringify(state)}\n`);
}
