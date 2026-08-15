/**
 * spool のドレイン。ロックを取れた1プロセスだけがここに入る。
 *
 * 順序の保証（CLAUDE.md「絶対に守ること」4）:
 * - spool は**到着順**に処理する
 * - ドレインが空振りするまで繰り返す。これが「解放完了後にもう一度 spool を見る」に当たり、
 *   走査直後に到着した分の取りこぼしを防ぐ
 */

import {
  formatPromptEvent,
  getEventHookName,
  getEventPromptId,
  getEventSessionId,
} from "../prompt/promptEventFormatter";
import { cleanTextForSpeech, splitIntoSentences } from "../text/textFilter";
import type { SpeechEntry } from "../core/speechLog";
import type { Emotion, SpeechRecord } from "../core/types";
import { assembleSentences } from "./messageAssembler";
import {
  cleanOrphans,
  readMessage,
  readProgress,
  readPromptPayload,
  removeEntry,
  scanSpool,
  writeProgress,
  type MessageContent,
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
  /**
   * 発話を確定させる。記録（speech.jsonl）と配信キュー（speech/）の両方に書く。
   * 採番はこの中で行われるので、採番済みのレコードが返る。
   */
  publish: (entries: SpeechEntry[]) => SpeechRecord[];
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

/** 1パスで読み込んだ spool の中身。session_id は保留解除の判定に要る */
type Loaded =
  | { entry: Extract<SpoolEntry, { kind: "message" }>; content: MessageContent }
  | { entry: Extract<SpoolEntry, { kind: "prompt" }>; payload: unknown };

function sessionIdOf(loaded: Loaded): string | null {
  return "content" in loaded ? loaded.content.sessionId : getEventSessionId(loaded.payload);
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

    // このパスで扱う分をまとめて読む。後続の session_id を見る必要があるので先に揃える
    const loaded: Loaded[] = entries.map((entry) =>
      entry.kind === "message"
        ? { entry, content: readMessage(entry.filePath) }
        : { entry, payload: readPromptPayload(entry.filePath) },
    );

    let changed = false;
    for (let i = 0; i < loaded.length; i++) {
      const item = loaded[i]!;
      const outcome =
        "content" in item
          ? processMessage(item, hasNewerInSameSession(loaded, i), deps)
          : processPrompt(item, deps, state, now);

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

/**
 * このメッセージがもう伸びないと判断してよいか。
 *
 * ★ 「後続エントリが1つでもあるか」で見てはいけない。`getSpoolDir()` にセッション成分が無く、
 *   `MessageDisplay` は matcher 非対応で**全セッションで発火する**ため、Claude Code を2枚開くと
 *   別セッションのメッセージで保留が解け、書きかけの断片が読み上げられて順序も壊れる。
 *
 * session_id が取れないものは判断材料にしない。保留したまま `final` を待つ方が安全。
 */
function hasNewerInSameSession(loaded: Loaded[], index: number): boolean {
  const sessionId = sessionIdOf(loaded[index]!);
  if (sessionId === null) return false;

  return loaded.slice(index + 1).some((other) => sessionIdOf(other) === sessionId);
}

interface EntryOutcome {
  written: number;
  /** 発話を書いた or spool を消した。もう一周する価値があるか */
  changed: boolean;
  stateDirty: boolean;
}

const NOTHING: EntryOutcome = { written: 0, changed: false, stateDirty: false };

function processMessage(
  item: Extract<Loaded, { content: MessageContent }>,
  hasNewer: boolean,
  deps: DrainDeps,
): EntryOutcome {
  const { entry, content } = item;
  const emitted = readProgress(entry.progressPath);

  const result = assembleSentences({
    deltas: content.deltas,
    emitted,
    final: content.final,
    flushPending: content.final || hasNewer,
  });

  let written = 0;
  if (result.sentences.length > 0) {
    const messageId = content.messageId ?? entry.messageId;
    deps.publish(
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
  item: Extract<Loaded, { payload: unknown }>,
  deps: DrainDeps,
  state: WorkerState,
  now: () => number,
): EntryOutcome {
  const { entry, payload } = item;

  // ★ 読めないものを消さないこと。hook の書き込み途中を掴んだだけかもしれない。
  //   恒久的に壊れているものは cleanOrphans が引き取る
  if (payload === null) return NOTHING;

  if (!deps.speakPrompts) {
    removeEntry(entry);
    return { ...NOTHING, changed: true };
  }

  const messages = formatPromptEvent(payload);
  if (messages.length === 0) {
    removeEntry(entry);
    return { ...NOTHING, changed: true };
  }

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
    withinWindow(at, state.pairedPromptAt, PROMPT_PAIR_WINDOW_MS)
  ) {
    state.pairedPromptId = null;
    removeEntry(entry);
    return { written: 0, changed: true, stateDirty: true };
  }

  const records: SpeechEntry[] = [];
  const sessionId = getEventSessionId(payload);

  for (const message of messages) {
    const cleaned = cleanTextForSpeech(message.text);
    if (!cleaned) continue;

    // 許可プロンプトは同じ文面で連続発火することがある
    if (cleaned === state.lastText && withinWindow(at, state.lastTextAt, DUPLICATE_WINDOW_MS)) continue;
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

  // ★ 書き込みが成功してから消す（processMessage と同じ順序）。
  //   先に消すと、append が失敗したときにイベントが復旧不能に失われる
  if (records.length > 0) deps.publish(records);
  removeEntry(entry);

  if (hookName === "PreToolUse" && promptId !== null) {
    state.pairedPromptId = promptId;
    state.pairedPromptAt = at;
    stateDirty = true;
  }

  return { written: records.length, changed: true, stateDirty };
}

/**
 * 抑制の時間窓に入っているか。
 *
 * ★ 経過が負なら窓の外として扱う。両タイムスタンプは `speak.state.json` に永続化されるので、
 *   サスペンド/レジュームや NTP で時計が巻き戻ると、本来ペアでない Notification を
 *   「ペア済み」と誤判定して**通知が二度と出なくなる**。
 */
function withinWindow(now: number, since: number, windowMs: number): boolean {
  const elapsed = now - since;
  return elapsed >= 0 && elapsed < windowMs;
}
