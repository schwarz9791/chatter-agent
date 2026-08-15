/**
 * spool のドレイン。ロックを取れた1プロセスだけがここに入る。
 *
 * 順序の保証（CLAUDE.md「絶対に守ること」4）:
 * - spool は**到着順**に処理する
 * - ドレインが空振りするまで繰り返す。これが「解放前にもう一度 spool を見る」に当たり、
 *   走査直後に到着した分の取りこぼしを防ぐ
 */

import { formatPromptEvent, getEventHookName, getEventPromptId } from "../prompt/promptEventFormatter";
import { cleanTextForSpeech, splitIntoSentences } from "../text/textFilter";
import type { SpeechEntry, SpeechLog } from "../core/speechLog";
import type { Emotion } from "../core/types";
import { assembleSentences } from "./messageAssembler";
import {
  cleanOrphans,
  readMessage,
  readProgress,
  readPromptPayload,
  removeEntry,
  scanSpool,
  writeProgress,
  type SpoolEntry,
} from "./spool";
import { readWorkerState, writeWorkerState, type WorkerState } from "./workerState";

/**
 * PreToolUse と、それに付随する Notification を同一プロンプトとみなす時間窓。
 * AskUserQuestion / ExitPlanMode では PreToolUse の直後に permission_prompt の
 * Notification も発火するため、後者を捨てるのに使う（上流 cc-mascot と同じ値）。
 */
const PROMPT_PAIR_WINDOW_MS = 10_000;

/** 同一テキストの連投を抑制する時間窓（許可プロンプトの重複発火対策） */
const DUPLICATE_WINDOW_MS = 3_000;

/** 空振りするまで回すが、万一 spool が育ち続けても抜けられるようにする */
const MAX_PASSES = 8;

export interface DrainDeps {
  spoolDir: string;
  speechLog: SpeechLog;
  workerStatePath: string;
  /** 応答待ち通知（kind: "prompt"）を読み上げるか */
  speakPrompts: boolean;
  /** これより無活動な spool は孤児として掃除する */
  spoolMaxAgeMs: number;
  classify: (text: string) => Emotion;
  now?: () => number;
}

export interface DrainResult {
  /** speech.jsonl に書いた行数 */
  written: number;
  passes: number;
  orphansRemoved: number;
}

export function drainSpool(deps: DrainDeps): DrainResult {
  const now = deps.now ?? Date.now;

  const orphansRemoved = cleanOrphans(deps.spoolDir, deps.spoolMaxAgeMs, now());

  const state = readWorkerState(deps.workerStatePath);
  let stateDirty = false;
  let written = 0;
  let passes = 0;

  for (; passes < MAX_PASSES; passes++) {
    const entries = scanSpool(deps.spoolDir);
    if (entries.length === 0) break;

    let changed = false;
    for (let i = 0; i < entries.length; i++) {
      // 後ろに別のイベントが控えているなら、このメッセージはもう伸びない。
      // 保留していた最後の文を先に流してよい（設計書からの上積み。§2-4 の 34〜80 秒を消す）
      const hasNewer = i < entries.length - 1;
      const outcome = processEntry(entries[i]!, hasNewer, deps, state, now);
      written += outcome.written;
      if (outcome.changed) changed = true;
      if (outcome.stateDirty) stateDirty = true;
    }

    // 何も動かなかった＝到着待ちだけが残っている。ここで抜ける
    if (!changed) break;
  }

  if (stateDirty) writeWorkerState(deps.workerStatePath, state);

  return { written, passes, orphansRemoved };
}

interface EntryOutcome {
  written: number;
  /** 発話を書いた or spool を消した。もう一周する価値があるか */
  changed: boolean;
  stateDirty: boolean;
}

const NOTHING: EntryOutcome = { written: 0, changed: false, stateDirty: false };

function processEntry(
  entry: SpoolEntry,
  hasNewer: boolean,
  deps: DrainDeps,
  state: WorkerState,
  now: () => number,
): EntryOutcome {
  return entry.kind === "message" ? processMessage(entry, hasNewer, deps) : processPrompt(entry, deps, state, now);
}

function processMessage(
  entry: Extract<SpoolEntry, { kind: "message" }>,
  hasNewer: boolean,
  deps: DrainDeps,
): EntryOutcome {
  const content = readMessage(entry.filePath);
  const emitted = readProgress(entry.progressPath);

  const result = assembleSentences({
    deltas: content.deltas,
    emitted,
    flushPending: content.final || hasNewer,
  });

  let written = 0;
  if (result.sentences.length > 0) {
    const messageId = content.messageId ?? entry.messageId;
    deps.speechLog.append(
      result.sentences.map((text): SpeechEntry => ({
        source: "claude-code",
        sessionId: content.sessionId,
        turnId: content.turnId,
        messageId,
        kind: "assistant",
        text,
        emotion: deps.classify(text),
      })),
    );
    written = result.sentences.length;
  }

  if (result.emitted !== emitted) writeProgress(entry.progressPath, result.emitted);

  // final:true を処理し終えたファイルだけを消す。まだ来ていなければ次の delta で読み直す
  if (content.final) removeEntry(entry);

  return { written, changed: written > 0 || content.final, stateDirty: false };
}

function processPrompt(
  entry: Extract<SpoolEntry, { kind: "prompt" }>,
  deps: DrainDeps,
  state: WorkerState,
  now: () => number,
): EntryOutcome {
  const payload = readPromptPayload(entry.filePath);

  // 1イベントで完結するので、読めても読めなくても必ず消す
  removeEntry(entry);

  if (!deps.speakPrompts || payload === null) return { ...NOTHING, changed: true };

  const messages = formatPromptEvent(payload);
  if (messages.length === 0) return { ...NOTHING, changed: true };

  let stateDirty = false;

  // AskUserQuestion / ExitPlanMode では PreToolUse の直後に permission_prompt の
  // Notification（英語の定型文）も発火する。同じ prompt_id のものを1回だけ捨てる。
  // prompt_id はツール単位ではなくユーザーのターン単位のIDに見えるため、
  // 「同じターンで質問の後に別途 Bash の許可プロンプトが出る」ケースを潰さないよう1回で打ち切る。
  const hookName = getEventHookName(payload);
  const promptId = getEventPromptId(payload);
  const at = now();

  if (
    hookName === "Notification" &&
    promptId !== null &&
    promptId === state.pairedPromptId &&
    at - state.pairedPromptAt < PROMPT_PAIR_WINDOW_MS
  ) {
    state.pairedPromptId = null;
    return { written: 0, changed: true, stateDirty: true };
  }

  let written = 0;
  const records: SpeechEntry[] = [];
  const sessionId =
    typeof (payload as { session_id?: unknown }).session_id === "string"
      ? (payload as { session_id: string }).session_id
      : null;

  for (const message of messages) {
    const cleaned = cleanTextForSpeech(message.text);
    if (!cleaned) continue;

    // 許可プロンプトは同じ文面で連続発火することがある
    if (cleaned === state.lastText && at - state.lastTextAt < DUPLICATE_WINDOW_MS) continue;
    state.lastText = cleaned;
    state.lastTextAt = at;
    stateDirty = true;

    for (const sentence of splitIntoSentences(cleaned)) {
      if (!sentence) continue;
      records.push({
        source: "claude-code",
        sessionId,
        turnId: null,
        messageId: null,
        kind: "prompt",
        text: sentence,
        emotion: deps.classify(sentence),
      });
    }
  }

  if (records.length > 0) {
    deps.speechLog.append(records);
    written = records.length;
  }

  if (hookName === "PreToolUse" && promptId !== null) {
    state.pairedPromptId = promptId;
    state.pairedPromptAt = at;
    stateDirty = true;
  }

  return { written, changed: true, stateDirty };
}
