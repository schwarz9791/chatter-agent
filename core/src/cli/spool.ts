/**
 * spool の走査と読み取り。
 *
 * ★ **hook の payload を解釈するのはこのファイルだけ。** `MessageDisplay` の入力スキーマは
 *   公式ドキュメントに記載が無く実測に基づくもの（設計書 §2-3・§10）なので、想定が外れたときに
 *   差し替える場所を1箇所に閉じてある。
 *
 * ★ **spool の `.jsonl` は絶対に書き換えない。** hook が並行して追記している。
 *   ワーカーが持つ進捗は `<message_id>.progress.json` のサイドカーに置く。
 */

import * as fs from "fs";
import * as path from "path";

const MESSAGE_SUFFIX = ".jsonl";
const PROMPT_PREFIX = "prompt-";
const PROMPT_SUFFIX = ".json";
const PROGRESS_SUFFIX = ".progress.json";

export interface MessageSpoolEntry {
  kind: "message";
  /** ファイル名そのもの。plugin が `<message_id>.jsonl` として置く */
  messageId: string;
  filePath: string;
  progressPath: string;
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
  sessionId: string | null;
  turnId: string | null;
  messageId: string | null;
}

function progressPathFor(filePath: string): string {
  return filePath.slice(0, -MESSAGE_SUFFIX.length) + PROGRESS_SUFFIX;
}

/**
 * 到着順のキー。
 *
 * ★ mtime を使わないこと。`<message_id>.jsonl` は delta ごとに追記されるので mtime が動き続け、
 *   先に始まったメッセージが後から追記されて順番が入れ替わる。birthtime が本来の「到着順」。
 *   birthtime を持たない環境では mtime に落とす。
 *
 * ★ ミリ秒（`birthtimeMs`）では粗すぎる。同じミリ秒に作られたファイルが同値になり、
 *   下のタイブレーク（パス順）に落ちて到着順が壊れる。`bigint: true` の統計情報が持つ
 *   ナノ秒を使う。ナノ秒は number の安全整数を超えるので bigint のまま扱う。
 */
function arrivalOrder(stat: fs.BigIntStats): bigint {
  return stat.birthtimeNs > 0n ? stat.birthtimeNs : stat.mtimeNs;
}

function classify(fileName: string, filePath: string, order: bigint): SpoolEntry | null {
  // サイドカーが prompt-*.json のグロブに混ざらないよう、最初に弾く
  if (fileName.endsWith(PROGRESS_SUFFIX)) return null;

  if (fileName.startsWith(PROMPT_PREFIX) && fileName.endsWith(PROMPT_SUFFIX)) {
    return { kind: "prompt", filePath, order };
  }

  if (fileName.endsWith(MESSAGE_SUFFIX)) {
    return {
      kind: "message",
      messageId: fileName.slice(0, -MESSAGE_SUFFIX.length),
      filePath,
      progressPath: progressPathFor(filePath),
      order,
    };
  }

  return null;
}

/** spool を到着順に走査する。ディレクトリが無いのは正常（まだ hook が動いていない） */
export function scanSpool(spoolDir: string): SpoolEntry[] {
  let fileNames: string[];
  try {
    fileNames = fs.readdirSync(spoolDir);
  } catch {
    return [];
  }

  const entries: SpoolEntry[] = [];
  for (const fileName of fileNames) {
    const filePath = path.join(spoolDir, fileName);
    let stat: fs.BigIntStats;
    try {
      stat = fs.statSync(filePath, { bigint: true });
    } catch {
      continue; // 走査中に消えた
    }
    if (!stat.isFile()) continue;

    const entry = classify(fileName, filePath, arrivalOrder(stat));
    if (entry) entries.push(entry);
  }

  // ナノ秒まで同値ならパスで決める（順序が実行ごとに揺れないように）
  return entries.sort((a, b) => {
    if (a.order !== b.order) return a.order < b.order ? -1 : 1;
    return a.filePath.localeCompare(b.filePath);
  });
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringOrNull(value: unknown): string | null {
  return typeof value === "string" && value ? value : null;
}

/** 壊れた行・途中で切れた行は黙って捨てる（hook が書き込み中の可能性がある） */
function parseLines(filePath: string): Record<string, unknown>[] {
  let text: string;
  try {
    text = fs.readFileSync(filePath, "utf-8");
  } catch {
    return [];
  }

  const out: Record<string, unknown>[] = [];
  for (const line of text.split("\n")) {
    if (!line.trim()) continue;
    try {
      const parsed: unknown = JSON.parse(line);
      if (isRecord(parsed)) out.push(parsed);
    } catch {
      // 追記が途中の行。次の起動で読み直す
    }
  }
  return out;
}

/**
 * `<message_id>.jsonl` を読み、index 順に結合できる delta 列にする。
 *
 * index に欠番があったら**そこで打ち切る**。歯抜けのまま繋ぐと文が壊れて読み上げられるので、
 * 欠けた分が届くまで（あるいは孤児掃除に回収されるまで）黙って待つ方を選ぶ。
 */
export function readMessage(filePath: string): MessageContent {
  const byIndex = new Map<number, Record<string, unknown>>();
  let sessionId: string | null = null;
  let turnId: string | null = null;
  let messageId: string | null = null;

  for (const payload of parseLines(filePath)) {
    const index = payload.index;
    if (typeof index !== "number" || !Number.isInteger(index) || index < 0) continue;
    // 同じ index が二度来たら後勝ち（hook の再送を想定）
    byIndex.set(index, payload);

    sessionId ??= stringOrNull(payload.session_id);
    turnId ??= stringOrNull(payload.turn_id);
    messageId ??= stringOrNull(payload.message_id);
  }

  const deltas: string[] = [];
  let final = false;
  for (let i = 0; byIndex.has(i); i++) {
    const payload = byIndex.get(i)!;
    deltas.push(typeof payload.delta === "string" ? payload.delta : "");
    if (payload.final === true) final = true;
  }

  return { deltas, final, sessionId, turnId, messageId };
}

/** `prompt-<…>.json` は1イベントで完結するので、payload をそのまま返す */
export function readPromptPayload(filePath: string): unknown {
  try {
    return JSON.parse(fs.readFileSync(filePath, "utf-8"));
  } catch {
    return null;
  }
}

/** 出力済みの文数。サイドカーが無ければ 0 */
export function readProgress(progressPath: string): number {
  try {
    const parsed: unknown = JSON.parse(fs.readFileSync(progressPath, "utf-8"));
    if (isRecord(parsed)) {
      const emitted = parsed.emitted;
      if (typeof emitted === "number" && Number.isInteger(emitted) && emitted >= 0) return emitted;
    }
  } catch {
    // 無い・壊れているなら 0 から。多少喋り直すが、黙るより良い
  }
  return 0;
}

export function writeProgress(progressPath: string, emitted: number): void {
  fs.writeFileSync(progressPath, `${JSON.stringify({ emitted })}\n`);
}

/** 処理し終えた spool を消す。メッセージはサイドカーごと消す */
export function removeEntry(entry: SpoolEntry): void {
  fs.rmSync(entry.filePath, { force: true });
  if (entry.kind === "message") fs.rmSync(entry.progressPath, { force: true });
}

/**
 * CLI が起動しないまま終わった孤児を掃除する。
 * 進行中のメッセージは delta のたびに mtime が更新されるので、ここでは mtime で「無活動時間」を見る。
 */
export function cleanOrphans(spoolDir: string, maxAgeMs: number, now: number = Date.now()): number {
  let fileNames: string[];
  try {
    fileNames = fs.readdirSync(spoolDir);
  } catch {
    return 0;
  }

  let removed = 0;
  for (const fileName of fileNames) {
    const filePath = path.join(spoolDir, fileName);
    try {
      if (now - fs.statSync(filePath).mtimeMs <= maxAgeMs) continue;
      fs.rmSync(filePath, { force: true });
      removed++;
    } catch {
      // 走査中に消えた
    }
  }
  return removed;
}
