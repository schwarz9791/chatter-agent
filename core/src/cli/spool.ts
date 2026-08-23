/**
 * spool の走査と読み取り。
 *
 * ★ **hook の payload を解釈するのはこのファイルだけ。** `MessageDisplay` の入力スキーマは
 *   公式ドキュメントに記載が無く実測に基づくもの（設計書 §2-3・§10）なので、想定が外れたときに
 *   差し替える場所を1箇所に閉じてある。
 *
 * ★ **spool のファイルは絶対に書き換えない。** hook は delta ごとに `<message_id>.<index>.json`
 *   を tmp + rename で置く（追記はしない — bash から任意長の追記を原子的にする移植可能な方法が
 *   無いため。→ docs/plugin.md）。1メッセージは複数の delta ファイルに分かれるので、
 *   「1メッセージ = 複数ファイル」を1エントリにまとめるのがこのファイルの仕事。
 *   **spool には**状態を持たない（`final` を見たときに全 delta から組み直す）。
 *
 * ★ これは「ワーカーが無状態」という意味ではない。`workerState.ts` は `speak.state.json`
 *   （`pairedPromptId` / `lastText` / tombstone）を `writeFileAtomic` で永続化している。
 *   この見出しを根拠に `read/writeWorkerState` を消すと、`AskUserQuestion` のたびに対の
 *   permission Notification を二重読みする退行が戻る。
 */

import * as fs from "fs";
import * as path from "path";

/** `<message_id>.<index>.json`。message_id はサニタイズ済みで `.` を含まない（plugin 側の責務） */
const MESSAGE_DELTA_RE = /^(.+)\.(\d+)\.json$/;
const PROMPT_PREFIX = "prompt-";
const PROMPT_SUFFIX = ".json";

export interface MessageSpoolEntry {
  kind: "message";
  /** ファイル名の `<message_id>` 部分 */
  messageId: string;
  /** delta ファイルのパス。**index 昇順**（欠番はありうる） */
  filePaths: string[];
  /** 到着順のキー。ナノ秒なので number に収まらない */
  order: bigint;
}

export interface PromptSpoolEntry {
  kind: "prompt";
  filePath: string;
  order: bigint;
}

export type SpoolEntry = MessageSpoolEntry | PromptSpoolEntry;

export interface MessageContent {
  /** index 0 から**連続している分だけ**。欠番の手前で打ち切る */
  deltas: string[];
  /** 連続部分の中に `final:true` があったか */
  final: boolean;
  /**
   * 連続接頭辞（`deltas`）に入らなかった、読めた delta が他にあるか。
   *
   * ★ `byIndex.size > deltas.length` で判定する。欠番の手前で打ち切っているだけで、
   *   欠番より後ろのファイルは実際には読めている（rename 済みで disk 上に存在する）ことを
   *   呼び出し側が知る術がこれまで無かった。救済経路（`worker.ts`）で publish してよいかの
   *   判定に使う: true なら「後ろにまだ読まれていない delta が残っている」ので、
   *   接頭辞だけを発話した上でその未読分まで消してはいけない。
   */
  hasGap: boolean;
  sessionId: string | null;
  turnId: string | null;
  messageId: string | null;
  /**
   * ユーザーのターン単位のID。`PreToolUse` / `Notification` の payload にも同じものが入る。
   *
   * ★ [#33] 発話順の是正にだけ使う。`MessageDisplay` と `PreToolUse` は別プロセスとして
   *   同時に走るので、どちらが先に spool へ着くかに保証が無い（実測で prompt が本文を
   *   316ms 追い越した）。`worker.ts` の `hoistMessagesBeforePrompt` が、この ID の一致で
   *   本文を prompt の前へ戻す。
   *
   * ★ 粒度は粗い（1 `prompt_id` に message が最大22件ぶら下がる実測）。「この質問の直前の
   *   本文はどれか」の特定には**使えない**。用途を広げないこと。
   */
  promptId: string | null;
}

/**
 * 到着順のキー。
 *
 * ★ mtime を使わないこと。メッセージの「到着順」を代表する値としては、個々のファイルの
 *   mtime ではなく birthtime を使う必要がある（後述 `arrivalOrderOfMessage`）。
 *   birthtime を持たない環境では mtime に落とす。
 *
 * ★ ミリ秒（`birthtimeMs`）では粗すぎる。同じミリ秒に作られたファイルが同値になり、
 *   下のタイブレーク（パス順）に落ちて到着順が壊れる。`bigint: true` の統計情報が持つ
 *   ナノ秒を使う。ナノ秒は number の安全整数を超えるので bigint のまま扱う。
 */
function arrivalOrder(stat: fs.BigIntStats): bigint {
  return stat.birthtimeNs > 0n ? stat.birthtimeNs : stat.mtimeNs;
}

type ClassifiedFile =
  | { kind: "prompt"; filePath: string; order: bigint }
  | { kind: "messageDelta"; messageId: string; index: number; filePath: string; order: bigint };

/**
 * 判定順は「prompt- → message」。
 *
 * ★ 廃止した進捗サイドカー（`prompt-<…>.progress.json` / `<message_id>.progress.json`）を
 *   弾く専用ガードはここには無い。`<message_id>.progress.json` は元々どちらのパターンにも
 *   一致しないので無視されるが、`prompt-<…>.progress.json` は `prompt-` 始まり・`.json` 終わりに
 *   一致するので prompt entry として拾われる。それでよい —
 *   `formatPromptEvent`（`prompt/promptEventFormatter.ts`）は未知 payload に `[]` を返すので、
 *   `worker.ts` の `processPrompt` が発話ゼロのまま即削除する。かつてここにあったガードは
 *   「掃除を孤児掃除の6時間まで遅らせているだけ」だったので外した（回収経路は
 *   `worker.test.ts` の該当テストを参照）。
 */
function classify(fileName: string, filePath: string, order: bigint): ClassifiedFile | null {
  if (fileName.startsWith(PROMPT_PREFIX) && fileName.endsWith(PROMPT_SUFFIX)) {
    return { kind: "prompt", filePath, order };
  }

  // `<message_id>.<index>.json`。tmp のまま（`*.json.tmp`）は末尾が `.json` にならないので
  // ここには来ない＝rename が終わるまで自然に無視される
  const match = MESSAGE_DELTA_RE.exec(fileName);
  if (match) {
    return { kind: "messageDelta", messageId: match[1]!, index: Number(match[2]), filePath, order };
  }

  return null;
}

/**
 * メッセージの到着順。index 0 のファイルの到着順を採る。
 *
 * ★ 「最新のファイルの到着順」ではなく「index 0 の到着順」であること。final:true は
 *   大きく遅れて届くため、遅れて増えた delta ファイルの方が新しくなるのが普通に起きる。
 *   それに引きずられて到着順が入れ替わらないよう、常に先頭（index 0）を基準にする。
 *
 * index 0 のファイルが（何らかの理由で）無ければ、手元にある中で最小の到着順を使う。
 */
function arrivalOrderOfMessage(deltas: { index: number; order: bigint }[]): bigint {
  const zero = deltas.find((d) => d.index === 0);
  if (zero) return zero.order;
  return deltas.reduce((min, d) => (d.order < min ? d.order : min), deltas[0]!.order);
}

/** タイブレーク（同じナノ秒のときに実行のたびに順序が揺れないようにする）用の代表パス */
function tieBreakPath(entry: SpoolEntry): string {
  return entry.kind === "prompt" ? entry.filePath : entry.filePaths[0]!;
}

/** spool を到着順に走査する。ディレクトリが無いのは正常（まだ hook が動いていない） */
export function scanSpool(spoolDir: string): SpoolEntry[] {
  let fileNames: string[];
  try {
    fileNames = fs.readdirSync(spoolDir);
  } catch {
    return [];
  }

  const prompts: PromptSpoolEntry[] = [];
  // messageId ごとに delta ファイルを集める。index 昇順への並べ替えとエントリ化は後段でまとめて行う
  const messages = new Map<string, { index: number; filePath: string; order: bigint }[]>();

  for (const fileName of fileNames) {
    const filePath = path.join(spoolDir, fileName);
    let stat: fs.BigIntStats;
    try {
      stat = fs.statSync(filePath, { bigint: true });
    } catch {
      continue; // 走査中に消えた
    }
    if (!stat.isFile()) continue;

    const classified = classify(fileName, filePath, arrivalOrder(stat));
    if (!classified) continue;

    if (classified.kind === "prompt") {
      prompts.push(classified);
      continue;
    }

    const list = messages.get(classified.messageId) ?? [];
    list.push({ index: classified.index, filePath: classified.filePath, order: classified.order });
    messages.set(classified.messageId, list);
  }

  const entries: SpoolEntry[] = [...prompts];
  for (const [messageId, deltas] of messages) {
    deltas.sort((a, b) => a.index - b.index);
    entries.push({
      kind: "message",
      messageId,
      filePaths: deltas.map((d) => d.filePath),
      order: arrivalOrderOfMessage(deltas),
    });
  }

  // ナノ秒まで同値ならパスで決める（順序が実行ごとに揺れないように）
  return entries.sort((a, b) => {
    if (a.order !== b.order) return a.order < b.order ? -1 : 1;
    return tieBreakPath(a).localeCompare(tieBreakPath(b));
  });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringOrNull(value: unknown): string | null {
  return typeof value === "string" && value ? value : null;
}

/** 1ファイル1 JSON を読む。壊れた・読めないファイルは黙って捨てる（hook が書き込み中の可能性がある） */
function readDeltaFile(filePath: string): Record<string, unknown> | null {
  let text: string;
  try {
    text = fs.readFileSync(filePath, "utf-8");
  } catch {
    return null;
  }
  try {
    const parsed: unknown = JSON.parse(text);
    return isRecord(parsed) ? parsed : null;
  } catch {
    return null; // 書き込み途中（rename 直前）を掴んだ可能性
  }
}

/**
 * delta ファイル群を読み、index 順に結合できる delta 列にする。
 *
 * index に欠番があったら**そこで打ち切る**。歯抜けのまま繋ぐと文が壊れて読み上げられるので、
 * 欠けた分が届くまで（あるいは孤児掃除に回収されるまで）黙って待つ方を選ぶ。
 *
 * ★ どの index かは**ファイル名ではなく payload の `index` フィールド**で判定する。
 *   ファイル名はグルーピング（scanSpool）にしか使わない。同じ index の payload が複数
 *   渡されたら後に読んだ方を勝たせる（呼び出し順は index 昇順で揃っているので、通常は
 *   ファイル名どおりの index と一致するが、そうでない場合への備え）。
 */
export function readMessage(filePaths: string[]): MessageContent {
  const byIndex = new Map<number, Record<string, unknown>>();
  let sessionId: string | null = null;
  let turnId: string | null = null;
  let messageId: string | null = null;
  let promptId: string | null = null;

  for (const filePath of filePaths) {
    const payload = readDeltaFile(filePath);
    if (!payload) continue;

    const index = payload.index;
    if (typeof index !== "number" || !Number.isInteger(index) || index < 0) continue;
    byIndex.set(index, payload);

    sessionId ??= stringOrNull(payload.session_id);
    turnId ??= stringOrNull(payload.turn_id);
    messageId ??= stringOrNull(payload.message_id);
    promptId ??= stringOrNull(payload.prompt_id);
  }

  const deltas: string[] = [];
  let final = false;
  for (let i = 0; byIndex.has(i); i++) {
    const payload = byIndex.get(i)!;
    deltas.push(typeof payload.delta === "string" ? payload.delta : "");
    if (payload.final === true) final = true;
  }

  const hasGap = byIndex.size > deltas.length;

  return { deltas, final, hasGap, sessionId, turnId, messageId, promptId };
}

/** `prompt-<…>.json` は1イベントで完結するので、payload をそのまま返す */
export function readPromptPayload(filePath: string): unknown {
  try {
    return JSON.parse(fs.readFileSync(filePath, "utf-8"));
  } catch {
    return null;
  }
}

/** 処理し終えた spool を消す。メッセージは全 delta ファイルを消す */
export function removeEntry(entry: SpoolEntry): void {
  if (entry.kind === "prompt") {
    fs.rmSync(entry.filePath, { force: true });
    return;
  }
  for (const filePath of entry.filePaths) fs.rmSync(filePath, { force: true });
}

/** delta ファイルを message_id でグルーピングするための鍵 */
function messageGroupKey(fileName: string): string | null {
  // prompt- は1イベント完結でグルーピングの必要が無いので対象外（呼び出し側でファイル単位に扱う）
  if (fileName.startsWith(PROMPT_PREFIX)) return null;

  const match = MESSAGE_DELTA_RE.exec(fileName);
  return match ? match[1]! : null;
}

/**
 * CLI が起動しないまま終わった孤児を掃除する。
 *
 * ★ **メッセージ単位でまとめて判定すること。** 1 delta 1 ファイルにすると、各ファイルの
 *   mtime は書かれた瞬間で止まる。ファイル単位で「無活動時間」を見ると、進行中メッセージの
 *   古い index のファイルだけが閾値を超えて消え、`index` に欠番ができて**そのメッセージが
 *   永久に発話されなくなる**。そのメッセージに属するファイル群の**最新 mtime**を見て、
 *   全体が無活動なら delta ファイルをまとめて消す。
 *
 * `prompt-*.json`、rename 前の孤立した `.tmp`、廃止した `*.progress.json` の残骸
 * （このグルーピングに掛からないもの）は、ファイル単位で判定する。
 */
export function cleanOrphans(spoolDir: string, maxAgeMs: number, now: number = Date.now()): number {
  let fileNames: string[];
  try {
    fileNames = fs.readdirSync(spoolDir);
  } catch {
    return 0;
  }

  const standalone: string[] = [];
  const messageGroups = new Map<string, string[]>();

  for (const fileName of fileNames) {
    const key = messageGroupKey(fileName);
    if (key === null) {
      standalone.push(fileName);
      continue;
    }
    const list = messageGroups.get(key) ?? [];
    list.push(fileName);
    messageGroups.set(key, list);
  }

  let removed = 0;

  for (const fileName of standalone) {
    const filePath = path.join(spoolDir, fileName);
    try {
      if (now - fs.statSync(filePath).mtimeMs <= maxAgeMs) continue;
      fs.rmSync(filePath, { force: true });
      removed++;
    } catch {
      // 走査中に消えた
    }
  }

  for (const groupFileNames of messageGroups.values()) {
    const filePaths = groupFileNames.map((fileName) => path.join(spoolDir, fileName));

    let latestMtimeMs = -Infinity;
    for (const filePath of filePaths) {
      try {
        const mtimeMs = fs.statSync(filePath).mtimeMs;
        if (mtimeMs > latestMtimeMs) latestMtimeMs = mtimeMs;
      } catch {
        // 走査中に消えた
      }
    }
    if (latestMtimeMs === -Infinity) continue; // グループ全体が既に消えていた
    if (now - latestMtimeMs <= maxAgeMs) continue;

    for (const filePath of filePaths) {
      try {
        fs.rmSync(filePath, { force: true });
        removed++;
      } catch {
        // 走査中に消えた
      }
    }
  }

  return removed;
}
