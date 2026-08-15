/**
 * 発話の配信キュー。1文1ファイルで、**ファイル名が `seq`**。
 *
 * ```
 * {root}/speech/000000000123.json    ← 中身は speech.jsonl の1行と同一
 * {root}/speech/000000000124.json
 * ```
 *
 * CLI がロック下で書き、server が読んで配信し、クライアントの ack で消える。
 *
 * ★ なぜ `speech.jsonl` の tail をやめたか。
 *   1つのファイルに記録と配信を兼ねさせると、**読み手だけが突出して複雑になる**。
 *   ローテートを跨ぐ差分読み取りは、世代交代の検出（inode か、サイズの逆行か）、
 *   退避された世代の未読部分の回収、消費バイト数の算術を同時に正しく保つ必要があり、
 *   実際に取りこぼしと二重配信を両方踏んだ。
 *
 *   キューにすると、順序はファイル名で決まり、消費は削除で表せる。
 *   **誰も tail しなくなるので、記録側のローテートの正しさも要求されなくなる。**
 *
 * `spool/` と同じ形。あちらは hook が書いて CLI が消し、こちらは CLI が書いて server が消す。
 *
 * ★ `list()` と `read()` に分けてあるのは、配信済みの entry を毎回全件 readFileSync
 *   させないため。server は 50ms ごとにポーリングするが、配信済みかどうかの絞り込みは
 *   呼び出し側で後から走るので、ファイル名の列挙だけで済む問い合わせと、実際に配信する
 *   1件だけの読み取りを分けないと、マスコットが繋がっていない間（ack が来ず trim が
 *   上限に張り付く）も毎秒何十回と全件を読み直すことになる。
 */

import * as fs from "fs";
import * as path from "path";
import { writeFileAtomic } from "./atomicWrite";
import type { SpeechRecord } from "./types";

/** ファイル名の桁数。`ls` で並べたときに順序が見えるよう固定幅にする */
const SEQ_DIGITS = 12;
const SUFFIX = ".json";
const TMP_SUFFIX = ".tmp";

export interface SpeechQueue {
  /** 採番済みのレコードをキューに積む。書けた件数を返す（1件の fs エラーで残りを巻き添えにしない） */
  enqueue(records: SpeechRecord[]): number;
  /** `seq` 昇順。中身は読まない */
  list(): number[];
  /** その seq の1行。読めない・空・JSON でない・payload の seq がファイル名と食い違うなら null */
  read(seq: number): string | null;
  /** `seq <= upTo` を消す。消した件数を返す */
  ackUpTo(upTo: number): number;
  /** mtime が `maxAgeMs` より古い entry を消す。落ちている間に書かれた分だけを捨てるためのもの */
  dropOlderThan(maxAgeMs: number, now?: number): number;
  /** 件数が上限を超えていたら、古い方から捨てる。捨てた件数を返す */
  trim(maxEntries: number): number;
  /** 落ちた enqueue が残した `.tmp` を消す。消した件数を返す */
  sweepTmp(): number;
}

function fileNameFor(seq: number): string {
  return `${String(seq).padStart(SEQ_DIGITS, "0")}${SUFFIX}`;
}

/**
 * ファイル名から `seq` を読む。
 *
 * ★ 中身の JSON はパースしない。順序を決めるだけなら名前で足りるし、
 *   1文ごとにパースするコストを server のポーリングに乗せたくない。
 */
function seqFromFileName(fileName: string): number | null {
  if (!fileName.endsWith(SUFFIX)) return null;

  const stem = fileName.slice(0, -SUFFIX.length);
  if (!/^\d+$/.test(stem)) return null;

  const seq = Number(stem);
  return Number.isSafeInteger(seq) ? seq : null;
}

export function createSpeechQueue(queueDir: string): SpeechQueue {
  fs.mkdirSync(queueDir, { recursive: true });

  /** ディレクトリを走査して `seq` 昇順に並べる。中身は読まない */
  function listSeqs(): { seq: number; fileName: string }[] {
    let fileNames: string[];
    try {
      fileNames = fs.readdirSync(queueDir);
    } catch {
      return [];
    }

    const found: { seq: number; fileName: string }[] = [];
    for (const fileName of fileNames) {
      const seq = seqFromFileName(fileName);
      if (seq !== null) found.push({ seq, fileName });
    }
    return found.sort((a, b) => a.seq - b.seq);
  }

  function remove(fileName: string): boolean {
    try {
      fs.rmSync(path.join(queueDir, fileName), { force: true });
      return true;
    } catch {
      return false;
    }
  }

  return {
    enqueue(records) {
      let written = 0;
      for (const record of records) {
        const target = path.join(queueDir, fileNameFor(record.seq));
        try {
          // ★ tmp + rename で書くこと（atomicWrite.ts）。server は 50ms ごとに
          //   readdir するので、素の書き込みだと書きかけを読まれる
          writeFileAtomic(target, `${JSON.stringify(record)}\n`);
          written++;
        } catch (err) {
          // 1件の fs エラーで残り全部を落とさない。呼び出し側が握り潰さないよう件数で伝える
          console.error(`[SpeechQueue] seq=${record.seq} の書き込みに失敗しました:`, err);
        }
      }
      return written;
    },

    list() {
      return listSeqs().map(({ seq }) => seq);
    },

    read(seq) {
      const filePath = path.join(queueDir, fileNameFor(seq));
      let line: string;
      try {
        line = fs.readFileSync(filePath, "utf-8").trim();
      } catch {
        return null; // 走査後に消えた・そもそも無い
      }
      if (!line) return null;

      // ★ payload の seq とファイル名の seq を照合する。サーバーはファイル名の seq で
      //   順序・配信済みかどうかの判定・ack 削除を決め、クライアントは payload の seq で重複排除する
      //   （protocol.md）。両者がずれると、どちらの側にもエラーが出ないまま実在する
      //   文を落とすか、同じ文を二度喋る。壊れたファイルをここで削除はしない
      //   （ユーザーが置いたものかもしれない。飛ばすだけにして判断は呼び出し側に残す）
      let parsed: unknown;
      try {
        parsed = JSON.parse(line);
      } catch {
        return null;
      }
      if (typeof parsed !== "object" || parsed === null) return null;
      if ((parsed as { seq?: unknown }).seq !== seq) return null;

      return line;
    },

    ackUpTo(upTo) {
      if (!Number.isSafeInteger(upTo) || upTo < 0) return 0;

      let removed = 0;
      for (const { seq, fileName } of listSeqs()) {
        if (seq > upTo) break; // 昇順なので、ここから先は対象外
        if (remove(fileName)) removed++;
      }
      return removed;
    },

    dropOlderThan(maxAgeMs, now = Date.now()) {
      let removed = 0;
      for (const { fileName } of listSeqs()) {
        let mtimeMs: number;
        try {
          mtimeMs = fs.statSync(path.join(queueDir, fileName)).mtimeMs;
        } catch {
          continue; // 走査中に消えた
        }
        if (now - mtimeMs <= maxAgeMs) continue; // まだ新しい（今まさに書かれた分かもしれない）。残す
        if (remove(fileName)) removed++;
      }
      return removed;
    },

    trim(maxEntries) {
      if (maxEntries < 0) return 0;

      const all = listSeqs();
      const excess = all.length - maxEntries;
      if (excess <= 0) return 0;

      // 古い発話は無価値なので、溢れたら古い方から捨てる。
      // クライアントが繋がっていなければ ack は来ないので、これが唯一の歯止めになる
      let removed = 0;
      for (const { fileName } of all.slice(0, excess)) {
        if (remove(fileName)) removed++;
      }
      return removed;
    },

    sweepTmp() {
      let fileNames: string[];
      try {
        fileNames = fs.readdirSync(queueDir);
      } catch {
        return 0;
      }

      let removed = 0;
      for (const fileName of fileNames) {
        if (!fileName.endsWith(`${SUFFIX}${TMP_SUFFIX}`)) continue;
        if (remove(fileName)) removed++;
      }
      return removed;
    },
  };
}
