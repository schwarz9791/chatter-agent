/**
 * `speech.jsonl` への追記・ローテート・`seq` 採番。
 *
 * **記録**であって配信経路ではない。配信は `speechQueue.ts` が持つ。
 *
 * ★ 誰もこのファイルを tail しないので、**ローテートの正しさが要求されない**。
 *   退避は1世代だけ（`speech.1.jsonl` を上書き）で、取りこぼしても困る人がいない。
 *   読み手がいた頃は、世代交代の検出と未読部分の回収を正しく保つ必要があった。
 *
 * 呼び出し側は**ロックを保持していること**。`seq` の採番と state の更新はロック下でしか行わない
 * （並列に走らせると発話順が入れ替わる。CLAUDE.md「絶対に守ること」4）。
 */

import * as fs from "fs";
import * as path from "path";
import { getSpeechLogBackupPath } from "./paths";
import type { SpeechRecord } from "./types";

/** `seq` と `ts` はこのモジュールが決めるので、呼び出し側は残りを渡す */
export type SpeechEntry = Omit<SpeechRecord, "seq" | "ts">;

export interface SpeechLogDeps {
  logPath: string;
  statePath: string;
  /** これを超えたら `speech.1.jsonl` に退避する */
  maxBytes: number;
  /** テストから時刻を固定するため */
  now?: () => Date;
}

export interface SpeechLog {
  /** 1文1行で追記し、採番済みのレコードを返す */
  append(entries: SpeechEntry[]): SpeechRecord[];
  /** 次に採番される seq（追記はしない） */
  peekNextSeq(): number;
}

/** 末尾から seq を拾うために読むバイト数。1行はたかだか数KBなのでこれで足りる */
const TAIL_READ_BYTES = 64 * 1024;

/** ファイル末尾の有効な行から seq を拾う。読めなければ 0 */
function readLastSeq(filePath: string): number {
  let fd: number;
  try {
    fd = fs.openSync(filePath, "r");
  } catch {
    return 0;
  }

  try {
    const size = fs.fstatSync(fd).size;
    if (size === 0) return 0;

    const length = Math.min(size, TAIL_READ_BYTES);
    const buffer = Buffer.allocUnsafe(length);
    fs.readSync(fd, buffer, 0, length, size - length);

    const lines = buffer.toString("utf-8").split("\n");
    // 先頭行は切れている可能性があるので、末尾から遡って最初に読めた行を採用する
    for (let i = lines.length - 1; i >= 0; i--) {
      const line = lines[i]?.trim();
      if (!line) continue;
      try {
        const parsed: unknown = JSON.parse(line);
        if (typeof parsed === "object" && parsed !== null) {
          const seq = (parsed as { seq?: unknown }).seq;
          if (typeof seq === "number" && Number.isInteger(seq)) return seq;
        }
      } catch {
        // 途中で切れた行。さらに遡る
      }
    }
    return 0;
  } finally {
    fs.closeSync(fd);
  }
}

function readStateNextSeq(statePath: string): number {
  try {
    const parsed: unknown = JSON.parse(fs.readFileSync(statePath, "utf-8"));
    if (typeof parsed === "object" && parsed !== null) {
      const next = (parsed as { nextSeq?: unknown }).nextSeq;
      if (typeof next === "number" && Number.isInteger(next) && next >= 1) return next;
    }
  } catch {
    // 無い・壊れているのは異常ではない。ログ末尾から復旧する
  }
  return 1;
}

function writeStateNextSeq(statePath: string, nextSeq: number): void {
  const tmp = `${statePath}.tmp`;
  fs.writeFileSync(tmp, `${JSON.stringify({ nextSeq })}\n`);
  fs.renameSync(tmp, statePath);
}

export function createSpeechLog(deps: SpeechLogDeps): SpeechLog {
  const { logPath, statePath, maxBytes } = deps;
  const now = deps.now ?? (() => new Date());
  const backupPath = getSpeechLogBackupPath(logPath);

  fs.mkdirSync(path.dirname(logPath), { recursive: true });

  /**
   * state と実ファイルの整合を取る。
   *
   * クラッシュで両者がずれると、seq の重複（クライアントの欠落検出が壊れる）か
   * 欠番（欠落の誤検知）になる。どちらに転んでも直せるよう、大きい方を採る。
   * ローテート直後は現世代が空なので、その場合だけ1世代前も見る。
   */
  function reconcile(): number {
    let lastSeq = readLastSeq(logPath);
    if (lastSeq === 0) lastSeq = readLastSeq(backupPath);
    return Math.max(readStateNextSeq(statePath), lastSeq + 1);
  }

  let nextSeq = reconcile();

  /** 退避は1世代だけ。前の退避は上書きされる */
  function rotate(): void {
    if (fs.existsSync(logPath)) fs.renameSync(logPath, backupPath);
  }

  function currentSize(): number {
    try {
      return fs.statSync(logPath).size;
    } catch {
      return 0;
    }
  }

  /**
   * 末尾が改行で終わっているか。
   *
   * 追記が途中で切れた（電源断など）ファイルに素直に追記すると、壊れた断片と次の
   * レコードが**1行に融合して**、正常なはずの行まで読めなくなる。改行を1つ挟んで
   * 断片を断片のまま隔離する。
   */
  function endsWithNewline(size: number): boolean {
    if (size === 0) return true;
    let fd: number;
    try {
      fd = fs.openSync(logPath, "r");
    } catch {
      return true;
    }
    try {
      const buffer = Buffer.allocUnsafe(1);
      fs.readSync(fd, buffer, 0, 1, size - 1);
      return buffer[0] === 0x0a;
    } finally {
      fs.closeSync(fd);
    }
  }

  return {
    peekNextSeq: () => nextSeq,

    append(entries) {
      if (entries.length === 0) return [];

      const ts = now().toISOString();
      const records: SpeechRecord[] = entries.map((entry) => ({
        seq: nextSeq++,
        ts,
        source: entry.source,
        sessionId: entry.sessionId,
        turnId: entry.turnId,
        messageId: entry.messageId,
        kind: entry.kind,
        text: entry.text,
        emotion: entry.emotion,
      }));

      const body = `${records.map((record) => JSON.stringify(record)).join("\n")}\n`;

      // 既に中身があり、この追記で上限を超えるなら先に退避する。
      // 空のファイルをローテートしても空の世代が増えるだけなので何もしない。
      let size = currentSize();
      if (size > 0 && size + Buffer.byteLength(body) > maxBytes) {
        rotate();
        size = 0;
      }

      const payload = endsWithNewline(size) ? body : `\n${body}`;

      // ログを先に書く。ここで落ちても reconcile が末尾から拾い直せる
      // （state を先に書くと、落ちたときに欠番だけが残って復旧の手がかりが消える）。
      fs.appendFileSync(logPath, payload);
      writeStateNextSeq(statePath, nextSeq);

      return records;
    },
  };
}
