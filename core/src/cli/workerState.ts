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

/**
 * tombstone の保持件数（有界リング）。
 *
 * `message_id` は UUID なので1件36バイト前後、64件でも数KBに収まる。古いものから溢れて
 * 捨ててよい（溢れた古い孤児のカスケードは `worker.ts` 側の「中身の無いエントリを
 * `hasNewer` の候補から外す」二重の防御で受ける）。
 */
const TOMBSTONE_LIMIT = 64;

export interface WorkerState {
  /** 直前に読み上げた PreToolUse の prompt_id。付随する Notification を1回だけ捨てるために使う */
  pairedPromptId: string | null;
  pairedPromptAt: number;
  /** 直前に読み上げた応答待ちのテキスト。連投を抑制する */
  lastText: string;
  lastTextAt: number;
  /**
   * publish し終えた `message_id` の有界リング（tombstone）。
   *
   * 救済経路（`final` 未着で `hasNewer` により発話する経路）で全 delta ファイルを消した後に、
   * 遅れて届いた delta が「同一セッションの後続」として `hasNewerInSameSession` に成立し、
   * まだ伸びている途中の次のメッセージを打ち切ってしまう孤児カスケードを止めるために持つ
   * （CLAUDE.md 承認済み計画 A-2）。ここに載っている `message_id` の delta は発話せず即破棄する。
   *
   * 副次的に、publish 直後にここへ書くことで exactly-once の記録にもなる:
   * `removeEntry` が失敗して spool ファイルが残っても、次のドレインで再 publish されない。
   */
  publishedMessageIds: string[];
}

export function emptyWorkerState(): WorkerState {
  return { pairedPromptId: null, pairedPromptAt: 0, lastText: "", lastTextAt: 0, publishedMessageIds: [] };
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
        publishedMessageIds: Array.isArray(record.publishedMessageIds)
          ? record.publishedMessageIds.filter((id): id is string => typeof id === "string").slice(-TOMBSTONE_LIMIT)
          : [],
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

/** `messageId` が publish 済みとして記録されているか（tombstone） */
export function isTombstoned(state: WorkerState, messageId: string): boolean {
  return state.publishedMessageIds.includes(messageId);
}

/**
 * `messageId` を publish 済みとして記録する。有界リングなので、上限を超えた分は
 * 古いものから捨てる（呼び出し元で `writeWorkerState` して永続化すること）。
 */
export function addTombstone(state: WorkerState, messageId: string): void {
  state.publishedMessageIds.push(messageId);
  if (state.publishedMessageIds.length > TOMBSTONE_LIMIT) {
    state.publishedMessageIds.splice(0, state.publishedMessageIds.length - TOMBSTONE_LIMIT);
  }
}
