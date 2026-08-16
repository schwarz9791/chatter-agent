/**
 * spool のドレイン。ロックを取れた1プロセスだけがここに入る。
 *
 * 順序の保証（CLAUDE.md「絶対に守ること」4）:
 * - spool は**到着順**に処理する
 * - 空振り（進展なし）が**2回連続**するまで繰り返す。1回目の空振りの後にもう一周させることが
 *   「解放完了後にもう一度 spool を見る」に当たり、直前の走査が終わった直後に到着した分の
 *   取りこぼしを防ぐ
 */

import {
  formatPromptEvent,
  getEventHookName,
  getEventPromptId,
  getEventSessionId,
} from "../prompt/promptEventFormatter";
import { cleanTextForSpeech, splitIntoSentences } from "../text/textFilter";
import { acquireLock, type Lock } from "../core/lock";
import type { SpeechEntry } from "../core/speechLog";
import type { Emotion, SpeechRecord } from "../core/types";
import { assembleSentences } from "./messageAssembler";
import {
  cleanOrphans,
  readMessage,
  readPromptPayload,
  removeEntry,
  scanSpool,
  type MessageContent,
  type SpoolEntry,
} from "./spool";
import { readWorkerState, writeWorkerState, type WorkerState } from "./workerState";

/**
 * ロック取得に使ってよい合計の待ち時間予算。
 *
 * ★ 長く待ってよい理由: CLI は hook からデタッチ起動されているので、ここで待っても hook 自体は
 *   ブロックしない。長く待っても実害は「node プロセスが数個並ぶ」だけ。
 * ★ 長く待つ必要がある理由: `final:true` の delta と `permission_prompt` の Notification は
 *   **そのターン最後の hook イベント**。ここでロックを取り損ねると、次に誰かが hook を
 *   発火させるまで発話が沈黙する。`AskUserQuestion` の場合、それは**ユーザーが既に回答した後**になる。
 *   旧予算（4回試行 × 120ms ≒ 360ms、Node の起動込みで実測 408〜420ms）は、先行 worker が
 *   ロックを 500ms 以上保持しただけで超えていた。実測されたドレイン所要時間に対して
 *   十分な余裕を持たせ、3秒を予算にする。
 */
export const LOCK_MAX_WAIT_MS = 3_000;

/** 再試行の間隔 */
const LOCK_RETRY_DELAY_MS = 120;

/** 同期で待つ。CLI は hook からデタッチ起動されているので、待っても hook はブロックしない */
function sleepSync(ms: number): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

export interface AcquireLockWithRetryOptions {
  maxWaitMs?: number;
  retryDelayMs?: number;
  /** テスト用。既定は実際に待つ `sleepSync` */
  sleep?: (ms: number) => void;
  /** テスト用 */
  now?: () => number;
}

/**
 * ロックを取る。取れなければ `LOCK_MAX_WAIT_MS` を使い切るまで再試行する。
 *
 * ★ 一度で諦めてはいけない。先行ワーカーが最後の走査を終えてから解放するまでの窓に
 *   届いた spool は、そのワーカーにも拾われず、こちらが即終了すると誰にも拾われない。
 */
export function acquireLockWithRetry(lockDir: string, options: AcquireLockWithRetryOptions = {}): Lock | null {
  const maxWaitMs = options.maxWaitMs ?? LOCK_MAX_WAIT_MS;
  const retryDelayMs = options.retryDelayMs ?? LOCK_RETRY_DELAY_MS;
  const sleep = options.sleep ?? sleepSync;
  const now = options.now ?? Date.now;

  const deadline = now() + maxWaitMs;
  for (;;) {
    const lock = acquireLock(lockDir);
    if (lock) return lock;
    if (now() >= deadline) return null;
    sleep(retryDelayMs);
  }
}

/**
 * PreToolUse と、それに付随する Notification を同一プロンプトとみなす時間窓。
 * AskUserQuestion / ExitPlanMode では PreToolUse の直後に permission_prompt の
 * Notification も発火するため、後者を捨てるのに使う（上流 cc-mascot と同じ値）。
 */
const PROMPT_PAIR_WINDOW_MS = 10_000;

/** 同一テキストの連投を抑制する時間窓（許可プロンプトの重複発火対策） */
const DUPLICATE_WINDOW_MS = 3_000;

/** 2回連続で空振りするまで回すが、万一 spool が育ち続けても抜けられるようにする */
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
  // 「進展なし」が連続した回数。1回目の空振りで即座に抜けると、そのパスの走査が
  // 終わった直後に届いた spool を見ないまま抜けてしまう（CLAUDE.md「絶対に守ること」4）。
  // 2回連続してはじめて「もう届く分は無い」とみなす
  let unchangedStreak = 0;

  for (; passes < MAX_PASSES; passes++) {
    const entries = scanSpool(deps.spoolDir);
    if (entries.length === 0) break;

    // このパスで扱う分をまとめて読む。後続の session_id を見る必要があるので先に揃える
    const loaded: Loaded[] = entries.map((entry) =>
      entry.kind === "message"
        ? { entry, content: readMessage(entry.filePaths) }
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

    if (changed) {
      unchangedStreak = 0;
      continue;
    }

    // 何も動かなかった。ここで即抜けると、この走査の直後に届いた分を見ないまま終わる。
    // もう一周だけ確認し、それでも空振りならようやく「到着待ちだけが残っている」と判断する
    unchangedStreak++;
    if (unchangedStreak >= 2) break;
  }

  if (stateDirty) writeWorkerState(deps.workerStatePath, state);

  return { written, passes, orphansRemoved };
}

/**
 * `final` が来なかったメッセージを、後続イベントの到着で救済してよいか。
 *
 * 通常の発話は `final:true` が駆動する。これはその取りこぼし（ESC 中断・クラッシュ・
 * index 欠番で `final` に到達できないメッセージ）を、次のイベントが来た時点で拾うための経路。
 *
 * ★ 「後続エントリが1つでもあるか」で見てはいけない。`getSpoolDir()` にセッション成分が無く、
 *   `MessageDisplay` は matcher 非対応で**全セッションで発火する**ため、Claude Code を2枚開くと
 *   別セッションのメッセージで救済が誤発火し、まだ伸びる途中のメッセージが打ち切られて
 *   読み上げられる（順序も壊れる）。
 *
 * session_id が取れないものは判断材料にしない。そのまま `final` を待つ方が安全。
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

  // ★ `final` を待つ（CLAUDE.md「絶対に守ること」1）。まだ閉じていないメッセージには触らない。
  //   `hasNewer` は `final` が来なかったメッセージの救済で、通常経路ではない
  if (!content.final && !hasNewer) return NOTHING;

  const sentences = assembleSentences(content.deltas);

  // ★ メッセージ1つ分をまとめて1回だけ publish すること。分けて呼ぶと `ts` が割れる
  //   （`speechLog.append` は呼び出しごとに1回だけ時刻を取る）。クライアントは
  //   `(seq, ts)` で重複排除する契約なので、`ts` の同値性は契約の一部（docs/protocol.md）
  if (sentences.length > 0) {
    const messageId = content.messageId ?? entry.messageId;
    deps.publish(
      sentences.map((text): SpeechEntry => ({
        source: "claude-code",
        sessionId: content.sessionId,
        turnId: content.turnId,
        messageId,
        kind: "assistant",
        text,
        emotion: deps.classify(text),
      })),
    );
  }

  // ★ 書き込みが成功してから消す（processPrompt と同じ順序）。先に消すと、publish が
  //   失敗したときにメッセージが復旧不能に失われる。
  // ★ 逆に、消さずに抜けてはいけない。進捗サイドカーが無くなったので、残した entry は
  //   次のドレインで丸ごと組み直されて**メッセージ全体が二度発話される**
  removeEntry(entry);

  return { written: sentences.length, changed: true, stateDirty: false };
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
