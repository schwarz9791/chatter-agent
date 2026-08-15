/**
 * `speech.jsonl` の差分読み取り。
 *
 * server は**判断ロジックを持たない**（docs/core.md）。ここも行の中身は解釈せず、
 * `?since=` の絞り込みに `seq` を読むだけに留める。行はそのまま WebSocket に流す。
 */

import * as fs from "fs";
import { getSpeechLogGenerationPath } from "../core/paths";

export interface SpeechTail {
  /** 前回以降に増えた行。**完結した行だけ**を返す */
  readNew(): string[];
  /**
   * `seq > since` の行を、現世代のファイルの**既に配信した範囲から**拾う。
   *
   * ★ ファイル全体を返してはいけない。`ws` は connection を発火する**前に**クライアントを
   *   `wss.clients` に入れるので、まだ配信していない行を backfill に含めると、直後に
   *   watcher が発火したときの broadcast と合わせて**新規クライアントだけが二重に受け取る**。
   *   読み取り位置より先の行は、接続済みのこのクライアントにも broadcast で届く。
   *
   * 遡れるのは現世代まで（設計書 §4-4）。それより古い `seq` を要求されたら手元の最古から返す。
   * 「戻れない」ことは各行の `seq` の飛びとしてクライアントに見えるので、
   * 契約に無い制御メッセージは足さない。
   */
  backfill(since: number): string[];
  /** 末尾まで読み飛ばす（起動時に、溜まっている分を配信しないため） */
  seekToEnd(): void;
  position(): number;
}

interface FileIdentity {
  size: number;
  /** POSIX の inode。Windows では 0 になりうるので、その場合はサイズだけで判定する */
  inode: number;
}

function identify(filePath: string): FileIdentity | null {
  try {
    const stat = fs.statSync(filePath);
    return { size: stat.size, inode: Number(stat.ino) };
  } catch {
    return null; // まだ CLI が一度も書いていない / ローテートの瞬間
  }
}

function readRange(filePath: string, from: number, to: number): Buffer {
  const length = to - from;
  if (length <= 0) return Buffer.alloc(0);

  const fd = fs.openSync(filePath, "r");
  try {
    const buffer = Buffer.allocUnsafe(length);
    const read = fs.readSync(fd, buffer, 0, length, from);
    return buffer.subarray(0, read);
  } finally {
    fs.closeSync(fd);
  }
}

const NEWLINE = 0x0a;

/**
 * 完結した行だけを取り出す。返り値の `consumed` は**実際に消費したバイト数**。
 *
 * ★ 文字列に直してから数えないこと。追記が途中で切れたファイルには不正な UTF-8 が残り
 *   （`speechLog.ts` の `endsWithNewline` が断片を断片のまま隔離する仕様）、デコードで
 *   U+FFFD に化けた分だけバイト数がずれて `position` が本来より進む。ずれた位置から
 *   読み直すと、世代まるごと再配信したり、先頭が欠けた行をそのまま流したりする。
 */
function completeLines(chunk: Buffer): { lines: string[]; consumed: number } {
  // 追記の途中に当たった可能性があるので、最後の改行までしか消費しない
  const lastNewline = chunk.lastIndexOf(NEWLINE);
  if (lastNewline === -1) return { lines: [], consumed: 0 };

  return {
    lines: chunk
      .subarray(0, lastNewline)
      .toString("utf-8")
      .split("\n")
      .filter((line) => line.trim().length > 0),
    consumed: lastNewline + 1,
  };
}

function seqOf(line: string): number | null {
  try {
    const parsed: unknown = JSON.parse(line);
    if (typeof parsed === "object" && parsed !== null) {
      const seq = (parsed as { seq?: unknown }).seq;
      if (typeof seq === "number" && Number.isInteger(seq)) return seq;
    }
  } catch {
    // 壊れた行。配信対象にしない
  }
  return null;
}

/**
 * 世代交代を検出したとき、退避先（`speech.1.jsonl`）に移った**直前世代の未読部分**を読む。
 *
 * 退避先の inode が読んでいたファイルと一致することを確かめてから読む。一致しなければ
 * 2世代以上が一度に流れたということなので、追わずに諦める（誤った世代を配信しない）。
 */
function readCarryOver(logPath: string, from: number, expectedInode: number): string[] {
  const rotatedPath = getSpeechLogGenerationPath(logPath, 1);
  const rotated = identify(rotatedPath);

  if (rotated === null) return [];
  if (expectedInode !== 0 && rotated.inode !== 0 && rotated.inode !== expectedInode) return [];
  if (rotated.size <= from) return [];

  return completeLines(readRange(rotatedPath, from, rotated.size)).lines;
}

export function createSpeechTail(logPath: string): SpeechTail {
  let position = 0;
  let inode = 0;

  return {
    position: () => position,

    seekToEnd() {
      const current = identify(logPath);
      position = current?.size ?? 0;
      inode = current?.inode ?? 0;
    },

    readNew() {
      const current = identify(logPath);

      // ローテートの瞬間はファイルが一時的に見えないことがある。
      // ★ ここで状態を捨てないこと。捨てると次の呼び出しで世代交代を検出できず、
      //   退避された世代の末尾（下の carried）を取りこぼす
      if (current === null) return [];

      // ★ 世代交代の検出（設計書 §6）。
      //   設計書は「サイズが読み取り位置より小さくなったら」としているが、それだけでは
      //   **新世代がたまたま同じサイズに達した瞬間**に読むと取りこぼす。inode を主に使い、
      //   inode が当てにならない環境（Windows）ではサイズの逆行で拾う。
      const rotated = inode !== 0 && current.inode !== 0 && current.inode !== inode;
      // inode が当てにならない環境（Windows）ではサイズの逆行でしか気づけない。
      // その経路でも carry-over を試せるよう、両方を「世代が変わった」として扱う
      const generationChanged = rotated || current.size < position;

      // ★ 退避された直前世代の、まだ読んでいない末尾を先に拾う。
      //   これが無いと、読み取りとローテートの間に書かれた行がそのまま消える。
      //   2世代以上が一度に流れた場合は追えないが、その欠落は seq の飛びとして
      //   クライアントに見える（?since= で埋め直せる）。
      const carried = generationChanged ? readCarryOver(logPath, position, inode) : [];

      if (generationChanged) position = 0;
      inode = current.inode;

      if (current.size === position) return carried;

      const { lines, consumed } = completeLines(readRange(logPath, position, current.size));
      position += consumed;

      return carried.length > 0 ? [...carried, ...lines] : lines;
    },

    backfill(since) {
      if (position === 0) return [];

      // 読み取り位置までしか読まない。その先は broadcast で届く（上のコメント参照）
      return completeLines(readRange(logPath, 0, position)).lines.filter((line) => {
        const seq = seqOf(line);
        return seq !== null && seq > since;
      });
    },
  };
}
