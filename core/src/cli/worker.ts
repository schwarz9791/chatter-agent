/**
 * spool のドレイン。ロックを取れた1プロセスだけがここに入る。
 *
 * 順序の保証（CLAUDE.md「絶対に守ること」4）:
 * - spool は**到着順**（`birthtime`）に処理する。**ただしそれだけでは発話順は決まらない** —
 *   `MessageDisplay` と `PreToolUse` は別プロセスとして同時に走るので、prompt が本文を
 *   追い越して着くことがある。`hoistMessagesBeforePrompt`（引き上げ）と `needsBodyWait`
 *   （本文待ち）の2つが、到着順の上でこれを補正する（[#33]）
 * - 空振り（進展なし）が**2回連続**するまで繰り返す。1回目の空振りの後にもう一周させることが
 *   「解放完了後にもう一度 spool を見る」に当たり、直前の走査が終わった直後に到着した分の
 *   取りこぼしを防ぐ
 *
 * [#33]: https://github.com/schwarz9791/chatter-agent/issues/33
 */

import {
  formatPromptEvent,
  getEventHookName,
  getEventPromptId,
  getEventSessionId,
} from "../prompt/promptEventFormatter";
import { cleanTextForSpeech, splitIntoSentences } from "../text/textFilter";
import { toSpeechSentences } from "../text/speechText";
import { acquireLock, type Lock } from "../core/lock";
import type { SpeechEntry } from "../core/speechLog";
import type { Emotion, SpeechRecord } from "../core/types";
import type { Summarize } from "../summarizer/types";
import { assembleSentences } from "./messageAssembler";
import {
  cleanOrphans,
  countSpoolMessageFiles,
  readMessage,
  readPromptPayload,
  removeEntry,
  scanSpool,
  type MessageContent,
  type SpoolEntry,
} from "./spool";
import {
  addSummarizerSession,
  addSummaryAttempt,
  addTombstone,
  isSummarizerSession,
  isSummaryAttempted,
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
 *
 * ★★ D3（issue #38 レビュー）で根拠を書き換えた。旧コメントは「ここでロックを取り損ねると、
 *   次に誰かが hook を発火させるまで発話が沈黙する」としていたが、これは `drainSpool` の
 *   多パス構造（下の for ループ、`unchangedStreak` が2になるまで `scanSpool` をやり直す）を
 *   勘定に入れていなかった。実際には、**長時間ロックを保持しているワーカーが、その間に
 *   届いた spool を同じドレインの次のパスで拾う。** ロックを取れなかったプロセスがここで
 *   諦めても、通知は失われず先行ワーカーが処理する（自分が処理するか先行ワーカーが処理するか
 *   の違いでしかなく、発話される時刻自体は変わらない）。
 *
 *   要約 ON では、先行ワーカーがロックを保持する時間が `aiSummaryMaxPerDrain ×
 *   aiSummaryTimeoutMs`（既定なら 3 回 × 60秒 = 180秒、設定の上限なら 8 回 × 60秒 = 480秒）
 *   まで伸びうる。それでも3秒で足りる理由は上と同じで、待つ側が3秒で諦めても要約中の
 *   先行ワーカーが自分のドレインの中で拾うため。
 *
 * ★ **定数 `3_000` は変えないこと。** 伸ばすと、delta が0.5〜3.5秒間隔で届くぶんだけ
 *   待機プロセス（各約49MB）が積み上がる一方、発話される時刻は上の理由により変わらない。
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

/**
 * ★ [#33] prompt を発話する前に、同一 `prompt_id` の本文が spool に着くのを待つ猶予
 * （`PROMPT_BODY_WAIT_POLLS × PROMPT_BODY_WAIT_POLL_MS` = **3秒**）。
 *
 * **引き上げ（`hoistMessagesBeforePrompt`）だけでは足りない。** 引き上げは「同じパスで両方が
 * 見えている」ことが前提だが、実測の典型ケースでは**本文がまだ spool に存在しない**:
 * `delta` は最後の flush を除いて行単位なので、改行で終わらない短い本文（1〜2文）は
 * **メッセージ全体が `final:true` の単一 delta で届く**（実機で採取した payload で確認）。
 * その手前で `PreToolUse` が着地して CLI を起こすと、引き上げる対象が無いまま質問だけが
 * 発話される。
 *
 * ★ **3秒の根拠は実測。ただし上限の証明ではない。**
 *
 *   | 実測 | `PreToolUse` → 本文の着地 |
 *   |---|---|
 *   | 2026-08-16（#33 起票時） | 316ms |
 *   | 2026-08-23 1回目 | 276ms |
 *   | 2026-08-23 3回目 | **約 550ms** |
 *
 *   最初は 500ms にしたが、3件目（550ms）で待ち切れずに逆転が再現した。ばらつきが大きく
 *   （`final` が届く時刻は「その手前でモデルが何をどれだけ生成したか」で決まる — docs/plugin.md）、
 *   **秒数を仕様として扱わないこと**。ここを縮めると逆転が戻る。
 *
 * ★ 待ってよい理由は `LOCK_MAX_WAIT_MS` と同じ。CLI は hook からデタッチ起動されているので、
 *   ここで待っても hook 自体はブロックしない。待つ側（本文の hook が起こした CLI）は
 *   `LOCK_MAX_WAIT_MS`（3秒）で諦めるが、**待っているワーカーが自分のドレインの中で拾う**ので
 *   発話は失われない（`LOCK_MAX_WAIT_MS` のヘッダにある「先行ワーカーが拾う」と同じ構造）。
 *
 * ★ 代償: **本文が伴わない質問でも、その prompt の発話が最大3秒遅れる。** `final` の待ちが
 *   中央値0秒・最悪で数十秒であることに比べれば無視できる、という判断で受け入れている。
 *
 * ★★ **待つのは `processPrompt` の直前であって、パスの先頭ではない**（PR #47 レビュー P1）。
 *   ここをパスの先頭に戻すと、次の3つが同時に壊れる:
 *
 *   1. **発話しない prompt にも3秒払う。** `speakPrompts: false` のときも、`formatPromptEvent`
 *      が `[]` を返す prompt（`AskUserQuestion` / `ExitPlanMode` 以外）のときも、待った末に
 *      `processPrompt` が捨てるだけになる → `needsBodyWait` が発話対象かを見ている理由
 *   2. **待ちが prompt だけでなくパス全部に乗る。** 同じパスに居た**完成済みメッセージ**まで
 *      巻き添えで3秒遅れる。`getSpoolDir()` にセッション成分が無いので**セッションを跨ぐ** —
 *      Claude Code を2枚開くと、片方の許可プロンプトがもう片方の完成済み発話を止める
 *   3. 待ちの予算が `boolean` だと、**無関係な delta で明けたときに待ち直せない**（下記）
 *
 * ★★ **予算は「ドレイン全体の残ポール数」で持つこと**（PR #47 レビュー P1）。`waitedForBody`
 *   のような boolean にすると、脱出条件（`countSpoolMessageFiles` が増えたか）が
 *   **待っている本文とは無関係な delta**で満たされたときに、やり直したパスで待ち直せず
 *   **逆転が戻る**。実運用でこれを踏む経路が2つある: Claude Code 2枚（spool がグローバル）と、
 *   要約 ON（要約 CLI 自身の `MessageDisplay` delta が同じ spool に落ちる）。
 *
 * ★ 時間ではなく**回数**で打ち切ること。`deps.now` はテストで固定値を返す（進まない）ので、
 *   `now() >= deadline` を終了条件にすると無限ループになる。合計 ms は
 *   `worker.test.ts` が `sleep` に渡された総和で assert している（`POLL_MS` を勝手に
 *   動かすと赤くなる）。
 */
export const PROMPT_BODY_WAIT_POLLS = 60;
export const PROMPT_BODY_WAIT_POLL_MS = 50;

/**
 * ★ [#33] 引き上げ（`hoistMessagesBeforePrompt`）が「その prompt より前に始まった本文」と
 *   みなす到着時刻の窓。ナノ秒（`SpoolEntry.order` の単位）。
 *
 * **待ちの上限と同じ値であること。** 理由はそちらのヘッダ ★★ を参照（待って捕まえる相手と
 * 引き上げてよい相手を同じ定義にする）。
 */
const HOIST_WINDOW_NS = BigInt(PROMPT_BODY_WAIT_POLLS * PROMPT_BODY_WAIT_POLL_MS) * 1_000_000n;

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
  /** テスト用。既定は実際に待つ `sleepSync`（[#33] の本文待ちで使う） */
  sleep?: (ms: number) => void;
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

/** ユーザーのターン単位のID。message / prompt のどちらの payload にも入っている（[#33]） */
function promptIdOf(loaded: Loaded): string | null {
  return "content" in loaded ? loaded.content.promptId : getEventPromptId(loaded.payload);
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

/**
 * state の永続化を try/catch で包む（issue #38 レビュー A4）。
 *
 * tombstone と spool 削除は**どちらか一方が成功すれば再 publish を防げる**（tombstone が
 * あれば `isTombstoned` で弾かれる。spool が無ければそもそも組み直されない）。ここを
 * 素の `writeWorkerState` のままにしておくと、throw したときに `processMessage` が
 * `tryRemoveEntry` の手前で抜けてしまい、「publish 済み・tombstone 未永続化・spool 残存」
 * という**両方失敗**の状態で `drainSpool` 全体が落ちる。次のドレインで同じメッセージが
 * 再 publish される（#30 でメッセージ単位にしたので、二重に読み上げられるのは1文ではなく
 * **メッセージ全文**）。ここで try/catch し、必ず `tryRemoveEntry` まで到達させることで、
 * 少なくとも spool 削除の方を成功させ、上の「どちらか一方」を満たす。
 *
 * ★★ **ここで包むのは「publish 後」の書き込みだけ。** `summarizeSentences` 内の
 *   `registerSessionId` コールバックからの `writeWorkerState` は**意図的に**包んでいない
 *   （そちらのコメント参照）。この非対称性を「統一しよう」と直すと、無限ループ防止の
 *   第2層が登録されないまま要約 CLI が起動する穴が開く。
 */
function tryWriteWorkerState(statePath: string, state: WorkerState): boolean {
  try {
    writeWorkerState(statePath, state);
    return true;
  } catch (err) {
    console.warn("[Worker] state の永続化に失敗しました:", err);
    return false;
  }
}

export function drainSpool(deps: DrainDeps): DrainResult {
  const now = deps.now ?? Date.now;
  const sleep = deps.sleep ?? sleepSync;

  const orphansRemoved = cleanOrphans(deps.spoolDir, deps.spoolMaxAgeMs, now());

  const state = readWorkerState(deps.workerStatePath);
  let stateDirty = false;
  let written = 0;
  let passes = 0;
  // 「進展なし」が連続した回数。1回目の空振りで即座に抜けると、そのパスの走査が
  // 終わった直後に届いた spool を見ないまま抜けてしまう（CLAUDE.md「絶対に守ること」4）。
  // 2回連続してはじめて「もう届く分は無い」とみなす
  let unchangedStreak = 0;
  // ★ [#33] 本文待ちの予算。**boolean にしないこと**（PROMPT_BODY_WAIT_POLLS のヘッダ ★★）。
  //   無関係な delta で待ちが明けたとき、残りを使って待ち直せる必要がある
  let remainingWaitPolls = PROMPT_BODY_WAIT_POLLS;

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

    // ★ [#33] prompt が本文を追い越して spool へ着いていたら、ここで並びを戻す。
    //   以降の処理（`hasNewerInSameSession` を含む）はすべてこの `ordered` を見ること
    const ordered = hoistMessagesBeforePrompt(loaded);

    let waited = false;

    for (let i = 0; i < ordered.length; i++) {
      const item = ordered[i]!;

      // ★ [#33] prompt に到達した。その本文がまだ spool に着いていないなら、**ここで**待つ。
      //   パスの先頭で待ってはいけない理由は PROMPT_BODY_WAIT_POLLS のヘッダ ★★ を参照
      //   （手前の完成済みメッセージは、この時点で既に publish されている）
      if (!("content" in item) && remainingWaitPolls > 0 && needsBodyWait(item, ordered, deps)) {
        remainingWaitPolls -= waitForBodyArrival(deps.spoolDir, remainingWaitPolls, sleep);
        waited = true;
        break; // 本文が着いたかもしれない。パスをやり直して引き上げからやる
      }

      const outcome =
        "content" in item
          ? processMessage(item, hasNewerInSameSession(ordered, i), deps, state)
          : processPrompt(item, deps, state, now);

      written += outcome.written;
      if (outcome.changed) changed = true;
      if (outcome.stateDirty) stateDirty = true;
    }

    // ★ `changed` を捨てないこと（PR #47 レビュー P3）。待ちで break したパスでも、
    //   その手前の tombstone 掃除・要約セッション掃除・publish は起きている。ここを飛ばすと
    //   「空振りが2回連続するまで繰り返す」が1回ぶん早く打ち切られる
    if (changed) unchangedStreak = 0;

    // ★ 待ちは「空振り」ではない。ここで unchangedStreak を進めると、本文の到着を待った
    //   ぶんだけドレインが早く終わる
    if (waited) continue;

    if (changed) continue;

    // 何も動かなかった。ここで即抜けると、この走査の直後に届いた分を見ないまま終わる。
    // もう一周だけ確認し、それでも空振りならようやく「到着待ちだけが残っている」と判断する
    unchangedStreak++;
    if (unchangedStreak >= 2) break;
  }

  // ★ A4: tryWriteWorkerState にしてある。ここが失敗しても drainSpool 全体を落とさない。
  //   ここに来る stateDirty は2種類ある:
  //     - `processPrompt` が立てた分（元からの設計。ペア判定の状態をまとめて1回で書く）
  //     - `processMessage` の永続化が失敗して `!persisted` を返した分（＝**ここが再試行の場**）
  //   後者がここでも失敗すると tombstone は永続化されないまま終わるが、そのメッセージの
  //   spool は `tryRemoveEntry` で消えているので、次のドレインで組み直されることはない
  //   （tombstone と spool 削除の「どちらか一方が成功すれば足りる」— `tryWriteWorkerState`
  //   のヘッダ参照）。state はメモリ上のものなので、プロセス終了で消えて次回には残らない
  if (stateDirty) tryWriteWorkerState(deps.workerStatePath, state);

  return { written, passes, orphansRemoved };
}

/**
 * ★ [#33] この prompt を発話する前に、本文の到着を待つべきか。
 *
 * 待つのは、**発話される prompt** に **同一セッション・同一 `prompt_id` の本文が伴っていない**
 * とき。判定の後半は引き上げ（`hoistMessagesBeforePrompt`）と同じ条件にしてある。
 *
 * ★ **発話しない prompt のために待たないこと**（PR #47 レビュー P1）。`speakPrompts: false` の
 *   ときも、`formatPromptEvent` が `[]` を返す prompt（`AskUserQuestion` / `ExitPlanMode` 以外の
 *   `PreToolUse`、廃止した進捗サイドカーの残骸など）のときも、待った末に `processPrompt` が
 *   `tryRemoveEntry` して捨てるだけになる。`formatPromptEvent` はここと `processPrompt` で
 *   2回呼ぶことになるが、純粋関数で payload 1件ぶんなので測って気にする対象ではない。
 *
 * ★ 「本文は既に発話済みなので待つ必要が無い」ケースも true になる（待ち損）。用途上、
 *   その prompt の発話が最大3秒遅れることの実害は無い（`final` の待ちは中央値 0秒 /
 *   最悪は数十秒）ので、判定を複雑にして**待つべきときに取りこぼす**方を避けている。
 *   待ちが乗るのは**この prompt 以降**だけで、手前の完成済みメッセージには乗らない
 *   （`drainSpool` が prompt に到達してから待つため）。
 */
function needsBodyWait(item: Extract<Loaded, { payload: unknown }>, ordered: Loaded[], deps: DrainDeps): boolean {
  if (!deps.speakPrompts) return false;
  if (item.payload === null) return false;
  if (formatPromptEvent(item.payload).length === 0) return false;

  const promptId = promptIdOf(item);
  const sessionId = sessionIdOf(item);
  if (promptId === null || sessionId === null) return false;

  return !ordered.some(
    (other) => "content" in other && promptIdOf(other) === promptId && sessionIdOf(other) === sessionId,
  );
}

/**
 * ★ [#33] 本文が spool に着くのを待つ。delta ファイルが1つでも増えたら即座に戻り、
 *   増えなければ `maxPolls` 回で諦める。**使ったポール数を返す**（呼び出し側が
 *   ドレイン全体の予算から差し引く）。
 *
 * ★ ここでは `prompt_id` の一致まで見ない。見るには delta ファイルを読む必要があり、
 *   ポーリングのたびに走らせるには重い。「何か増えた」で呼び出し側にパスをやり直させ、
 *   正確な判定はそちらの `needsBodyWait` / `hoistMessagesBeforePrompt` に任せる。
 *
 * ★★ **粗いぶん、無関係な delta でも明ける**（別セッション、要約 CLI 自身の出力）。それでも
 *   逆転が戻らないのは、呼び出し側が**予算を残ポール数で持っていて待ち直せる**から
 *   （`PROMPT_BODY_WAIT_POLLS` のヘッダ ★★）。ここを boolean 1回に戻すと、この粗さが
 *   そのまま「1回の無関係な delta で待ちが終わり、質問が本文を追い越す」に化ける。
 *
 * ★ 件数は `countSpoolMessageFiles`（`readdirSync` 1回）で取る。`scanSpool` は全ファイルに
 *   bigint `statSync` を打つので、ロックを握ったまま最大60回回すには重すぎる。
 *   **前後で同じ関数を使うこと** — 基準と比較先のソースがずれると、`tryRemoveEntry` が
 *   失敗して残ったファイルを片方だけが数え、1ポール目で待ちが明ける。
 */
function waitForBodyArrival(spoolDir: string, maxPolls: number, sleep: (ms: number) => void): number {
  const before = countSpoolMessageFiles(spoolDir);

  for (let polls = 1; polls <= maxPolls; polls++) {
    sleep(PROMPT_BODY_WAIT_POLL_MS);
    if (countSpoolMessageFiles(spoolDir) > before) return polls;
  }

  return maxPolls;
}

/**
 * ★ [#33] prompt が同一 `prompt_id` の本文を追い越して spool に着いていたら、本文を prompt の
 *   前へ引き上げる。
 *
 * `MessageDisplay` と `PreToolUse` は**別プロセスとして同時に走る**ので、どちらが先に spool へ
 * 着くかに保証が無い。`scanSpool` は到着順（`birthtime`）に並べるだけなので、prompt が先に
 * 着けばそのまま先に発話される — 実機で「質問を読み上げてから、その質問に至る説明を読み上げる」
 * 逆転を観測している（短い本文で `PreToolUse` − `final` = −316ms）。→ docs/plugin.md
 *
 * ★ **引き上げるだけでよい。** prompt が後ろに回れば `hasNewerInSameSession` がそのまま成立し、
 *   既存の救済経路（`processMessage` の `hasNewer`）が本文を publish する。`processMessage`
 *   側には手を入れない。
 *
 * ★ `prompt_id` の粒度は粗く、「この質問の直前の本文はどれか」までは特定できない
 *   （1 `prompt_id` に message が最大22件ぶら下がる実測）。**それで足りる。** 直したいのは
 *   「prompt が到着順で本文を追い越した」ケースだけで、そのとき spool に残っている同一
 *   `prompt_id` の本文は、ほぼ常にその prompt より前に始まったもの。複数あってもすべて先に
 *   出せば順序は正しくなる。
 *
 * ★★ **「ほぼ常に」の例外を時間窓で外している**（PR #47 レビュー P2）。1ターンに prompt が
 *   2つ出ることはある（`PROMPT_PAIR_WINDOW_MS` のコメント自身が「同じターンで質問の後に別途
 *   Bash の許可プロンプトが出る」ケースを認めている）。そのとき
 *
 *       [本文A, 許可プロンプトP1, 本文B, AskUserQuestion P2]   ← すべて同じ prompt_id
 *
 *   が同じパスに乗ると、`j > i` を無条件に舐める実装では `[A, B, P1, P2]` になり、
 *   **B が P1 を追い越す** — #33 と同じ形の逆転が別の場所で起きる。
 *
 *   区別の材料は到着時刻しかない。**追い越しは数百ms**（実測 276〜550ms）なのに対し、
 *   prompt の後に始まった本文はユーザーの応答時間を挟むので桁が違う。そこで
 *   `HOIST_WINDOW_NS` を超えて後に着いた本文は引き上げない。
 *
 *   窓を待ちの上限（`PROMPT_BODY_WAIT_POLLS × PROMPT_BODY_WAIT_POLL_MS`）と同じ値にするのは、
 *   **「待って捕まえる相手」と「引き上げてよい相手」を同じ定義にする**ため。別々の定数に
 *   すると、待ちだけ伸ばして引き上げが追随しない（＝待って捕まえたのに並べ替えない）ズレが入る。
 *
 * ★ `session_id` の一致も要求する（`hasNewerInSameSession` と同じ理由 — spool は
 *   グローバルに1ディレクトリで、Claude Code を2枚開けば別セッションの分が混ざる）。
 *   どちらかが取れないものは動かさない。そのまま到着順に従う方が安全。
 *
 * ★ 元の相対順序は保つ。引き上げた本文同士は到着順のまま並ぶ。
 */
function hoistMessagesBeforePrompt(loaded: Loaded[]): Loaded[] {
  const result: Loaded[] = [];
  const hoisted = new Set<number>();

  for (let i = 0; i < loaded.length; i++) {
    if (hoisted.has(i)) continue;

    const item = loaded[i]!;
    if ("content" in item) {
      result.push(item);
      continue;
    }

    const promptId = promptIdOf(item);
    const sessionId = sessionIdOf(item);

    if (promptId !== null && sessionId !== null) {
      for (let j = i + 1; j < loaded.length; j++) {
        // ★ 既に引き上げ済みのものを二度積まないこと。`AskUserQuestion` では PreToolUse と
        //   直後の Notification が**同じ `prompt_id`** で2つ並ぶので、この判定が無いと
        //   同じ本文が2回 result に入り、メッセージ全文が二重に発話される
        if (hoisted.has(j)) continue;

        const other = loaded[j]!;
        if (!("content" in other)) continue;
        if (promptIdOf(other) !== promptId || sessionIdOf(other) !== sessionId) continue;

        // ★ 時間窓の外＝この prompt より**後に始まった**本文とみなして引き上げない（上の ★★）。
        //   `loaded` は order 昇順なので `j > i` の範囲では差は常に非負
        if (other.entry.order - item.entry.order > HOIST_WINDOW_NS) continue;

        result.push(other);
        hoisted.add(j);
      }
    }

    result.push(item);
  }

  return result;
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
 *
 * ★★ [#46] **prompt に無条件 `true` を返すことには既知の代償がある。** `PreToolUse` と `final`
 *   の到着には数百 ms のズレがあり（実測 −316ms）、その窓でドレインが走ると
 *   **final flush でしか来ない最終行が spool に無いまま**救済が発火して tombstone が打たれる。
 *   遅れて届いた final の delta は孤児として破棄され、最終行は無言で失われる。
 *
 * ★★ ここを `false` にすれば最終行は守れるが、[#33] の引き上げ（`hoistMessagesBeforePrompt`）が
 *   救済経路に乗っているので、発話順の逆転が戻る。**順序の正しさと最終行の生存が
 *   トレードオフになっている。** 片方だけを見て直さないこと。
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

/** `summarizeSentences` の戻り値（issue #38 レビュー E1）。 */
interface SummarizeResult {
  /** 実際に publish する文の列（要約後 or フォールバックした元の `sentences`） */
  spoken: string[];
  /**
   * 要約が実際に効いたか。true のときだけ、呼び出し元は原文（要約前の全文）由来の感情を
   * 全文で共有してよい（E1 参照）。
   */
  summarized: boolean;
}

/**
 * 長いメッセージを要約する（issue #31）。`processMessage` の `assembleSentences` の直後、
 * `deps.publish` の手前に挟む。
 *
 * ★ A3（issue #38 レビュー）: 呼び出し元（`processMessage`）は `content.final` のときだけ
 *   この関数を呼ぶ。救済経路（`!content.final && hasNewer`）では呼ばれない —
 *   そちらのコメント（`processMessage` 側）を参照。
 *
 * ★ `processPrompt` からは呼ばない（そちら側にコメントあり）。
 */
function summarizeSentences(
  sentences: string[],
  deps: DrainDeps,
  state: WorkerState,
  messageId: string,
): SummarizeResult {
  // 文が0本なら要約を呼ぶ意味が無い（空文字列を渡しても閾値判定で弾かれるだけ）
  if (sentences.length === 0) return { spoken: sentences, summarized: false };

  // ★ A4（issue #38 レビュー）: 再要約を高々1回に抑える。要約中（数十秒）に親プロセスが
  //   死ぬと（OOM、maxBuffer 到達、Claude Code の終了、スリープ）、tombstone も spool 削除も
  //   されないので、次のドレインが同じメッセージを再 assemble してここへまた来る。要約 CLI が
  //   親を落とすタイプの障害だと、これがドレインのたびに繰り返され、数十秒を無限に燃やし
  //   続ける。★ 発話は絶対に落とさない: スキップするのは要約だけで、sentences はそのまま返す
  if (isSummaryAttempted(state, messageId)) return { spoken: sentences, summarized: false };

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
  //   （無限ループ防止の第2層が素通しになる）。
  //
  // ★★ ここは意図的に try/catch で**包まない**（A4 の非対称性。`tryWriteWorkerState` の
  //   ヘッダと対で読むこと）。ここで throw すると `summaryPipeline.ts` の `createSummaryPipeline`
  //   が返す関数を包んでいる catch が拾って原文返却になり、`execFileSync` に到達しない。
  //   つまり「session id を永続化できなければ
  //   要約 CLI を起動しない」という無限ループ防止・第2層の安全側の挙動が、この書き方（包まない）
  //   で成立している。ここを try/catch で包むと、登録されないまま CLI が起動する穴が開く。
  //   将来「publish 後の書き込み（`tryWriteWorkerState`）と統一しよう」と直されると危険。
  const summary = deps.summarize(original, (sessionId) => {
    addSummarizerSession(state, sessionId);
    addSummaryAttempt(state, messageId); // ← A4 で追加。writeWorkerState は1回のまま
    writeWorkerState(deps.workerStatePath, state);
  });

  // ★ `deps.summarize` を try/catch で包まない。`Summarize`（summarizer/types.ts）は
  //   「throw しない」を型で宣言した契約で、実装（summaryPipeline.ts）が本体を丸ごと try で
  //   包んで保証している。ここで握ると「throw しうる」という誤ったシグナルが残ってしまう

  // 要約しなかった／フォールバックした（原文をそのまま返した）。無駄な再整形をしない
  if (summary === original) return { spoken: sentences, summarized: false };

  // ★ A2（issue #38 レビュー）: cleanTextForSpeech / splitIntoSentences の直呼びをやめ、
  //   toSpeechSentences（text/speechText.ts）に一本化した。これで要約結果も
  //   truncateAtUnstableTail を通るようになり、未閉じのコードフェンスや表の行が
  //   要約結果に含まれていても読み上げに漏れない（旧実装はここだけこの防御を素通りしていた）
  const resplit = toSpeechSentences(summary);

  // ★ 要約結果が記号だけだった等で全部落ちたら、元の文をそのまま返す。ここでフォールバック
  //   しないと、メッセージが無言で消える（発話が丸ごと欠落し、しかも publish しないので
  //   ログにも残らない）。要約の失敗は「要約前の発話」に倒すべきで、無発話に倒してはいけない
  if (resplit.length === 0) return { spoken: sentences, summarized: false };

  return { spoken: resplit, summarized: true };
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

  const messageId = content.messageId ?? entry.messageId;

  // ★ A3（issue #38 レビュー）: 要約するのは content.final のときだけ。救済経路
  //   （!content.final && hasNewer）は「続きが来ないと確定した」わけではなく「次のイベントが
  //   来たので打ち切った」だけなので、ここで数十秒かけて要約する筋が無い。さらに、待って
  //   いる間に届いた本物の final:true は、その頃には tombstone が打たれているので次の
  //   ドレインで破棄される。要約 ON では待ち時間が約1ms → 数十秒に広がるので、完全な
  //   メッセージを取りこぼす確率が桁違いに上がる（要約されなかった場合と違い、二度と発話
  //   されない形で消える）
  const { spoken, summarized } = content.final
    ? summarizeSentences(sentences, deps, state, messageId)
    : { spoken: sentences, summarized: false };

  // ★ E1（issue #38 レビュー）: 要約が効いたときだけ、原文（要約前の全文）由来の感情を
  //   全文で共有する。RuleBasedEmotionClassifier の文末パターン
  //   （emotion/ruleBasedEmotionClassifier.ts の sentenceEndPatterns）はほぼ全部が
  //   ！/？/…/♪/絵文字で、要約プロンプトの指示に従ってモデルが記号を落とすと、要約後の
  //   文はほぼ確実に neutral に潰れる。要約が効かなかった（summarized: false）ときは
  //   従来どおり文ごとに判定する（文ごとに表情が変わるのが正しい）
  const sharedEmotion = summarized ? deps.classify(sentences.join("\n")) : null;

  // ★ メッセージ1つ分をまとめて1回だけ publish すること。分けて呼ぶと `ts` が割れる
  //   （`speechLog.append` は呼び出しごとに1回だけ時刻を取る）。クライアントは
  //   `(seq, ts)` で重複排除する契約なので、`ts` の同値性は契約の一部（docs/protocol.md）
  if (spoken.length > 0) {
    deps.publish(
      spoken.map((text): SpeechEntry => ({
        source: "claude-code",
        sessionId: content.sessionId,
        turnId: content.turnId,
        messageId,
        kind: "assistant",
        text,
        emotion: sharedEmotion ?? deps.classify(text),
      })),
    );
  }

  // ★ 書き込み順序が肝（CLAUDE.md 承認済み計画 A-3(b)）。publish → tombstone を永続化 →
  //   removeEntry の順にする。こうすると removeEntry が失敗しても、次のドレインで
  //   再 publish されない（tombstone が exactly-once の記録になる）。
  //
  // ★ A4（issue #38 レビュー）: writeWorkerState を tryWriteWorkerState に変えた。素の
  //   writeWorkerState が throw すると、この後の tryRemoveEntry に到達できず「publish 済み・
  //   tombstone 未永続化・spool 残存」という最悪の状態で drainSpool 全体が落ち、次のドレインで
  //   同じメッセージが再 publish される（二重発話。#30 でメッセージ単位にしたので被害は
  //   メッセージ全文）。tombstone と spool 削除はどちらか一方が成功すれば再 publish を防げるので
  //   （tombstone があれば isTombstoned で弾かれ、spool が無ければそもそも組み直されない）、
  //   ここでは「失敗しても必ず tryRemoveEntry まで進む」ことを優先する。
  addTombstone(state, entry.messageId);
  const persisted = tryWriteWorkerState(deps.workerStatePath, state);

  // ★ 書き込みが成功してから消す（processPrompt と同じ順序）。先に消すと、publish が
  //   失敗したときにメッセージが復旧不能に失われる。
  // ★ 逆に、消さずに抜けてはいけない。進捗サイドカーが無くなったので、残した entry は
  //   次のドレインで丸ごと組み直されようとする（tombstone があるので実際には再発話は
  //   されないが、spool にファイルが残り続けてしまう）
  tryRemoveEntry(entry);

  // ★ A4: state の永続化に失敗していれば stateDirty を立てて、drainSpool 末尾の
  //   tryWriteWorkerState で再試行させる
  return { written: spoken.length, changed: true, stateDirty: !persisted };
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
