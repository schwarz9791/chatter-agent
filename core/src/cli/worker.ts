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
import type { Summarize } from "../summarizer/types";
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
import {
  addSummarizerSession,
  addTombstone,
  isSummarizerSession,
  isTombstoned,
  readWorkerState,
  writeWorkerState,
  type WorkerState,
} from "./workerState";

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
  /**
   * 長いメッセージを要約する。**throw しない**（→ summarizer/types.ts の Summarize）。
   *
   * 既定値は持たせない。渡し忘れを型で落とすため（要約が無言でスキップされる方が、
   * 未設定に気付けないよりまし）
   */
  summarize: Summarize;
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

/**
 * spool の削除を try/catch で包む（CLAUDE.md 承認済み計画 A-3(d)）。
 *
 * `removeEntry`（`fs.rmSync(..., { force: true })`）は ENOENT は飲むが、EACCES / EPERM /
 * EROFS では throw する。ここで拾わないと `drainSpool` 全体が止まり、そのパスの後続
 * メッセージ・応答待ち通知まで処理されなくなる。
 *
 * tombstone（`workerState.ts`）が exactly-once の記録を担うので、削除に失敗して
 * spool ファイルが残っても、次のドレインで再 publish されることはない。
 */
function tryRemoveEntry(entry: SpoolEntry): void {
  try {
    removeEntry(entry);
  } catch (err) {
    console.warn("[Worker] spool の削除に失敗しました。次のドレインでも残ります:", err);
  }
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
    const rawLoaded: Loaded[] = entries.map((entry) =>
      entry.kind === "message"
        ? { entry, content: readMessage(entry.filePaths) }
        : { entry, payload: readPromptPayload(entry.filePath) },
    );

    let changed = false;

    // ★ tombstone（CLAUDE.md 承認済み計画 A-3(b)、最優先）。publish 済みの message_id への
    //   遅延 delta は、発話せず即破棄し `hasNewerInSameSession` の候補にもしない。
    //   ここで弾いておかないと、救済で全削除された孤児が「同一セッションの後続」として
    //   成立してしまい、まだ伸びている途中の次のメッセージを打ち切ってしまう
    //   （孤児カスケード。詳細は workerState.ts の WorkerState.publishedMessageIds）。
    const loaded: Loaded[] = [];
    for (const item of rawLoaded) {
      if ("content" in item && isTombstoned(state, item.entry.messageId)) {
        tryRemoveEntry(item.entry);
        changed = true;
        continue;
      }

      // ★ 無限ループ防止の第2層（workerState.ts の summarizerSessionIds 参照）。要約 CLI は
      //   `claude -p` をヘッドレス実行するので、その出力自身が MessageDisplay hook を発火させうる。
      //   第1層（CHATTER_AGENT_DISABLE=1 を付けて要約 CLI を spawn する）が本命で、これはそれが
      //   効かなかったときの保険。tombstone と違い message 限定にする理由が無いので、
      //   message / prompt の両方に効かせる（要約 CLI が応答待ち通知を出すことは無いはずだが、
      //   経路を非対称にする理由が無い）。★ ここで弾いて loaded に入れないことが重要:
      //   hasNewerInSameSession の候補計算はこの後の loaded を見るので、ここで弾かないと
      //   要約セッションの delta が「同一セッションの後続」として救済の材料に混ざってしまう
      const sessionId = sessionIdOf(item);
      if (sessionId !== null && isSummarizerSession(state, sessionId)) {
        tryRemoveEntry(item.entry);
        changed = true;
        continue;
      }

      loaded.push(item);
    }

    for (let i = 0; i < loaded.length; i++) {
      const item = loaded[i]!;
      const outcome =
        "content" in item
          ? processMessage(item, hasNewerInSameSession(loaded, i), deps, state)
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

  return loaded.slice(index + 1).some((other) => countsAsNewer(other) && sessionIdOf(other) === sessionId);
}

/**
 * 「新しいイベントが来た」の材料として数えてよいか（CLAUDE.md 承認済み計画 A-3(c)）。
 *
 * ★ 中身の無いメッセージエントリ（`deltas` が空）は候補から外す。tombstone の取りこぼし
 *   （クラッシュ・state の破損・有界リングから溢れた古い孤児）が起きても、それだけで
 *   カスケードが起きないための二重の防御。
 *
 *   spool の書き込みは tmp + rename なので、可視のファイルは常に完全である。にもかかわらず
 *   `deltas` が空になるのは「閉じたメッセージの遅延分（index 0 が既に消えた孤児）」か
 *   「一過性の読み取り失敗」のどちらかで、どちらも「次のメッセージを打ち切ってよい理由」には
 *   ならない。
 */
function countsAsNewer(loaded: Loaded): boolean {
  return "content" in loaded ? loaded.content.deltas.length > 0 : true;
}

interface EntryOutcome {
  written: number;
  /** 発話を書いた or spool を消した。もう一周する価値があるか */
  changed: boolean;
  stateDirty: boolean;
}

const NOTHING: EntryOutcome = { written: 0, changed: false, stateDirty: false };

/**
 * 長いメッセージを要約する（issue #31）。`processMessage` の `assembleSentences` の直後、
 * `deps.publish` の手前に挟む。
 *
 * ★ `final` 経由でも救済経路（`hasNewer` で打ち切ったメッセージ）でも通す。救済されたメッセージは
 *   本来より短い状態で閾値判定されるが、issue #31「決めること4」で許容と決定済み
 *   （要約は長いメッセージだけが対象で、常に要約される必要はない。短く打ち切られて閾値を
 *   下回れば単に要約されないだけで、発話そのものは救済経路のとおり出る）。
 *
 * ★ `processPrompt` からは呼ばない（そちら側にコメントあり）。
 */
function summarizeSentences(sentences: string[], deps: DrainDeps, state: WorkerState): string[] {
  // 文が0本なら要約を呼ぶ意味が無い（空文字列を渡しても閾値判定で弾かれるだけ）
  if (sentences.length === 0) return sentences;

  // ★ join("") ではなく join("\n") にすること。splitIntoSentences（text/textFilter.ts）は
  //   改行でも文を割るので、空文字で繋ぐと改行区切りだった文同士が地続きになり、要約に渡す
  //   文章の切れ目が消えてしまう。ただし副作用として、閾値判定に使う文字数が改行の分だけ
  //   水増しされる（文が10本なら区切りの改行9文字ぶん）。閾値ちょうど付近では誤差になりうるが、
  //   「切れ目を保つ」方を優先する
  //
  // ★ 閾値（`aiSummaryThreshold`）が当たるのはこの連結結果 — つまり **`cleanTextForSpeech` を
  //   通した後**のテキストの長さになる（`assembleSentences` が整形済みの文を返すため）。
  //   コードブロックや URL が落ちた後の「実際に読み上げる文字数」で判定されるので、
  //   生の Markdown がどれだけ長くても整形後に閾値を下回れば要約されない。これは意図した挙動で、
  //   要約する理由が「読み上げが長すぎること」である以上、判定も読み上げる長さで行うのが正しい
  const original = sentences.join("\n");

  // ★ registerSessionId は要約 CLI を execFileSync する**前**に呼ばれる契約（summarizer/types.ts）。
  //   ここで即座に永続化する。`drainSpool` 末尾の `writeWorkerState` に任せて遅延させると、
  //   要約中に親（chatter-agent-speak）が落ちたときにこのセッションIDが登録されないまま、
  //   要約 CLI 自身の spool 出力が残り、次のドレインで読み上げられてしまう
  //   （無限ループ防止の第2層が素通しになる）
  const summary = deps.summarize(original, (sessionId) => {
    addSummarizerSession(state, sessionId);
    writeWorkerState(deps.workerStatePath, state);
  });

  // ★ `deps.summarize` を try/catch で包まない。`Summarize`（summarizer/types.ts）は
  //   「throw しない」を型で宣言した契約で、実装（summaryPipeline.ts）が本体を丸ごと try で
  //   包んで保証している。ここで握ると「throw しうる」という誤ったシグナルが残ってしまう

  // 要約しなかった／フォールバックした（原文をそのまま返した）。無駄な再整形をしない
  if (summary === original) return sentences;

  const resplit = splitIntoSentences(cleanTextForSpeech(summary)).filter((s) => s.length > 0);

  // ★ 要約結果が記号だけだった等で全部落ちたら、元の文をそのまま返す。ここでフォールバック
  //   しないと、メッセージが無言で消える（発話が丸ごと欠落し、しかも publish しないので
  //   ログにも残らない）。要約の失敗は「要約前の発話」に倒すべきで、無発話に倒してはいけない
  if (resplit.length === 0) return sentences;

  return resplit;
}

function processMessage(
  item: Extract<Loaded, { content: MessageContent }>,
  hasNewer: boolean,
  deps: DrainDeps,
  state: WorkerState,
): EntryOutcome {
  const { entry, content } = item;

  // ★ `final` を待つ（CLAUDE.md「絶対に守ること」1）。まだ閉じていないメッセージには触らない。
  //   `hasNewer` は `final` が来なかったメッセージの救済で、通常経路ではない
  if (!content.final && !hasNewer) return NOTHING;

  // ★ 救済経路では「完全に読めた」ときだけ publish する（CLAUDE.md 承認済み計画 A-3(a)）。
  //   deltas が空 = index 0 が読めなかった（EMFILE 等の一過性かもしれない。ファイルを
  //   消してはいけない）。欠番あり = 欠番より後ろのファイルがまだ一度も読まれていない。
  //   publish すると接頭辞だけ発話した上で未読ファイルまで消えるので、方針は「全損維持」。
  //   次のドレインで欠番が埋まれば final 経由で全文が出る
  //   （spool.test.ts「★ index に欠番があるうちは何も出ない。埋まったら全文出る」）。
  if (!content.final && (content.deltas.length === 0 || content.hasGap)) return NOTHING;

  // ★ 救済経路（続きが来るかもしれない）では、文として閉じていない末尾を発話しない
  //   （CLAUDE.md 承認済み計画 A-3(e)）。`final` 経由（もう続きは来ない）ではそのまま全部出す
  const sentences = assembleSentences(content.deltas, { dropUnterminatedTail: !content.final });

  // 長いメッセージはここで要約する（issue #31）。0本ならそのまま、要約が効かなければ
  // 元の sentences がそのまま返る（summarizeSentences 参照）
  const spoken = summarizeSentences(sentences, deps, state);

  // ★ メッセージ1つ分をまとめて1回だけ publish すること。分けて呼ぶと `ts` が割れる
  //   （`speechLog.append` は呼び出しごとに1回だけ時刻を取る）。クライアントは
  //   `(seq, ts)` で重複排除する契約なので、`ts` の同値性は契約の一部（docs/protocol.md）
  if (spoken.length > 0) {
    const messageId = content.messageId ?? entry.messageId;
    deps.publish(
      spoken.map((text): SpeechEntry => ({
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

  // ★ 書き込み順序が肝（CLAUDE.md 承認済み計画 A-3(b)）。publish → tombstone を永続化 →
  //   removeEntry の順にする。こうすると removeEntry が失敗しても、次のドレインで
  //   再 publish されない（tombstone が exactly-once の記録になる）。
  addTombstone(state, entry.messageId);
  writeWorkerState(deps.workerStatePath, state);

  // ★ 書き込みが成功してから消す（processPrompt と同じ順序）。先に消すと、publish が
  //   失敗したときにメッセージが復旧不能に失われる。
  // ★ 逆に、消さずに抜けてはいけない。進捗サイドカーが無くなったので、残した entry は
  //   次のドレインで丸ごと組み直されようとする（tombstone があるので実際には再発話は
  //   されないが、spool にファイルが残り続けてしまう）
  tryRemoveEntry(entry);

  return { written: spoken.length, changed: true, stateDirty: false };
}

/**
 * ★ ここでは要約を呼ばない（issue #31）。応答待ち通知（kind: "prompt"）は長さによらず素通しする。
 *   質問文や許可プロンプトを要約すると意味が変わってしまう（cc-mascot も同じ扱い）。
 */
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
    tryRemoveEntry(entry);
    return { ...NOTHING, changed: true };
  }

  const messages = formatPromptEvent(payload);
  if (messages.length === 0) {
    tryRemoveEntry(entry);
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
    tryRemoveEntry(entry);
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
  tryRemoveEntry(entry);

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
