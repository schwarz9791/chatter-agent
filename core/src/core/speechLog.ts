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
import { writeFileAtomic } from "./atomicWrite";
import { getSpeechLogBackupPath } from "./paths";
import { isValidEpoch, LEGACY_EPOCH, type SpeechEpoch, type SpeechRecord } from "./types";

/** `epoch` / `seq` / `ts` はこのモジュールが決めるので、呼び出し側は残りを渡す */
export type SpeechEntry = Omit<SpeechRecord, "epoch" | "seq" | "ts">;

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
  /** この採番の世代。→ `SpeechEpoch` */
  readonly epoch: SpeechEpoch;
  /**
   * このプロセスが epoch を新規生成した ＝ **採番がやり直された**。
   *
   * 呼び出し側（`cli/publish.ts`）は、これが true なら最初の `enqueue` の前に配信キューを
   * 空にすること。旧世代の entry を残すと、`speechQueue.trim` が seq 昇順で捨てるせいで
   * **今書いたばかりの新しい entry から先に消える**（`server/dispatcher.ts` の `delivered` の
   * コメントが名指ししている罠）。
   */
  readonly epochIsNew: boolean;
}

/** 末尾から seq を拾うために読むバイト数。1行はたかだか数KBなのでこれで足りる */
const TAIL_READ_BYTES = 64 * 1024;

/**
 * 新しい採番世代の識別子を作る。
 *
 * ★ **`crypto` を top-level import しないこと。** `import { randomUUID } from "crypto"` は
 *   静的 ESM import なので、`chatter-agent-speak` の起動を**毎 delta 約2.6ms** 重くする
 *   （issue #43 の実測。CLI は hook から毎 delta 起動される）。ここは `speech.state.json` が
 *   無いときの1回きりの経路なので、**分岐の中で `globalThis.crypto` を触る**なら
 *   その1回しか払わない。
 *
 * 要求は「採番がやり直されるたびに違う値になること」だけで、順序も暗号学的性質も要らない。
 * ただし **URL に載る**（`/audio/<epoch>-<seq>.wav`）ので、推測しにくい方が望ましい
 * — ブラウザの `<audio src>` は `Origin` を送らず、サーバーの Origin 検査を素通りする。
 */
function generateEpoch(): SpeechEpoch {
  const uuid = globalThis.crypto?.randomUUID?.();
  if (isValidEpoch(uuid)) return uuid;
  // ★ `Date.now()` + `Math.random()` のような**推測できる値へフォールバックしないこと。**
  //   上のとおり epoch は URL に載り、`<audio src>` は Origin 検査を素通りするので、
  //   推測しにくさが LAN 上での実質的な最後の防御になっている。
  //   WebCrypto は Node 19 以降グローバルにあり、`engines` は >=24.11 なので、
  //   ここに来る時点でサポート外のランタイム。黙って弱い値を配るより、理由を出して止まる
  throw new Error("globalThis.crypto.randomUUID がありません（Node 24.11 以上が必要です）");
}

/**
 * ファイル末尾の有効な行から `seq` と `epoch` を拾う。読めなければ `{ seq: 0, epoch: null }`。
 *
 * `seq` は**最後の有効行**から採る。`epoch` はその行に無ければ**同じファイルの中でさらに
 * 遡って**探す。
 *
 * ★ **ファイルを跨いで組を作らないこと。** `speech.jsonl` と `speech.1.jsonl` から
 *   別々に拾うと、別の世代の `seq` と `epoch` がペアになる。
 *
 * ★ **同一ファイル内で遡るのは安全。** 1つの `speech.jsonl` に2つの世代は入らない
 *   （epoch が変わる条件は state とログの**両方**が消えることで、そのとき新しいログは
 *   空から始まる）。逆に、遡らないと**新しい CLI の後に古い CLI を一度走らせた**だけで
 *   （ロールバック / bisect）本物の epoch が `LEGACY_EPOCH` に降格し、接続中の
 *   クライアントが「採番のやり直し」と読んで**既に喋った発話をもう一度喋る**。
 */
function readLastEntry(filePath: string): { seq: number; epoch: SpeechEpoch | null } {
  const none = { seq: 0, epoch: null };

  let fd: number;
  try {
    fd = fs.openSync(filePath, "r");
  } catch {
    return none;
  }

  try {
    const size = fs.fstatSync(fd).size;
    if (size === 0) return none;

    const length = Math.min(size, TAIL_READ_BYTES);
    const buffer = Buffer.allocUnsafe(length);
    fs.readSync(fd, buffer, 0, length, size - length);

    const lines = buffer.toString("utf-8").split("\n");
    // 先頭行は切れている可能性があるので、末尾から遡って最初に読めた行を採用する
    let lastSeq: number | null = null;
    for (let i = lines.length - 1; i >= 0; i--) {
      const line = lines[i]?.trim();
      if (!line) continue;

      let parsed: unknown;
      try {
        parsed = JSON.parse(line);
      } catch {
        continue; // 途中で切れた行。さらに遡る
      }
      if (typeof parsed !== "object" || parsed === null) continue;
      const { seq, epoch } = parsed as { seq?: unknown; epoch?: unknown };

      // ★ isSafeInteger であること。speechQueue のファイル名解釈・wsServer.parseAck と
      //   同じ基準に揃えないと、壊れた state の値がここだけ通って採番に使われる
      //   （1e300 は isInteger では true になる）
      if (lastSeq === null && typeof seq === "number" && Number.isSafeInteger(seq)) lastSeq = seq;
      if (lastSeq === null) continue;

      // epoch はこの機能より前に書かれた行には無い。その行で止めずに遡る（doc 参照）
      if (isValidEpoch(epoch)) return { seq: lastSeq, epoch };
    }
    return lastSeq === null ? none : { seq: lastSeq, epoch: null };
  } finally {
    fs.closeSync(fd);
  }
}

function readState(statePath: string): { nextSeq: number; epoch: SpeechEpoch | null } {
  try {
    const parsed: unknown = JSON.parse(fs.readFileSync(statePath, "utf-8"));
    if (typeof parsed === "object" && parsed !== null) {
      const { nextSeq, epoch } = parsed as { nextSeq?: unknown; epoch?: unknown };
      const valid = typeof nextSeq === "number" && Number.isSafeInteger(nextSeq) && nextSeq >= 1;
      return {
        nextSeq: valid ? nextSeq : 1,
        epoch: isValidEpoch(epoch) ? epoch : null,
      };
    }
  } catch {
    // 無い・壊れているのは異常ではない。ログ末尾から復旧する
  }
  return { nextSeq: 1, epoch: null };
}

function writeState(statePath: string, nextSeq: number, epoch: SpeechEpoch): void {
  writeFileAtomic(statePath, `${JSON.stringify({ nextSeq, epoch })}\n`);
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
   *
   * epoch も同じ2つの情報源から拾う。**「採番がやり直された」と「epoch が変わった」を
   * 一致させる**のがここの唯一の仕事:
   *
   * - どちらかから epoch が読めた → そのまま使う（採番は続いている）
   * - epoch は読めないが seq は復旧できた → `LEGACY_EPOCH`（アップグレードの初回。
   *   ここで生成すると in-flight の発話が消える。→ `LEGACY_EPOCH` のコメント）
   * - どちらも復旧できなかった（`nextSeq === 1`）→ **やり直しなので新規生成**
   */
  function reconcile(): { nextSeq: number; epoch: SpeechEpoch; epochIsNew: boolean } {
    const state = readState(statePath);
    const current = readLastEntry(logPath);
    // ★ 退避側を見る条件は2つ。`seq` が拾えなかったとき（ローテート直後で現世代が空）と、
    //   **`epoch` だけが拾えなかったとき**。後者を落とすと、現世代の末尾が古い CLI の
    //   行で終わっているだけで、退避側に残っている本物の epoch を見ずに降格させる
    const needsBackup = current.seq === 0 || current.epoch === null;
    const backup = needsBackup ? readLastEntry(backupPath) : { seq: 0, epoch: null };
    const last = current.seq === 0 ? backup : current;

    const next = Math.max(state.nextSeq, last.seq + 1);
    const known = state.epoch ?? current.epoch ?? backup.epoch;
    if (known !== null) return { nextSeq: next, epoch: known, epochIsNew: false };
    if (next > 1) return { nextSeq: next, epoch: LEGACY_EPOCH, epochIsNew: false };
    return { nextSeq: next, epoch: generateEpoch(), epochIsNew: true };
  }

  const initial = reconcile();
  let nextSeq = initial.nextSeq;
  const epoch = initial.epoch;

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
    epoch,
    epochIsNew: initial.epochIsNew,

    peekNextSeq: () => nextSeq,

    append(entries) {
      if (entries.length === 0) return [];

      const ts = now().toISOString();
      const records: SpeechRecord[] = entries.map((entry) => ({
        epoch,
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
      writeState(statePath, nextSeq, epoch);

      return records;
    },
  };
}
