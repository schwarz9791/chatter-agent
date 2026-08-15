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
   * `seq > since` の行を、現世代のファイルから拾う。
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

function fileSize(filePath: string): number {
  return identify(filePath)?.size ?? 0;
}

function readRange(filePath: string, from: number, to: number): string {
  const length = to - from;
  if (length <= 0) return "";

  const fd = fs.openSync(filePath, "r");
  try {
    const buffer = Buffer.allocUnsafe(length);
    const read = fs.readSync(fd, buffer, 0, length, from);
    return buffer.subarray(0, read).toString("utf-8");
  } finally {
    fs.closeSync(fd);
  }
}

/** 完結した行だけを取り出す。返り値の `consumed` は「消費したバイト数」 */
function completeLines(chunk: string): { lines: string[]; consumed: number } {
  // 追記の途中に当たった可能性があるので、最後の改行までしか消費しない。
  // UTF-8 が境界で切れていても、切り捨てる側に入るので壊れない
  const lastNewline = chunk.lastIndexOf("\n");
  if (lastNewline === -1) return { lines: [], consumed: 0 };

  return {
    lines: chunk
      .slice(0, lastNewline)
      .split("\n")
      .filter((line) => line.trim().length > 0),
    consumed: Buffer.byteLength(chunk.slice(0, lastNewline + 1), "utf-8"),
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

      // ★ 退避された直前世代の、まだ読んでいない末尾を先に拾う。
      //   これが無いと、読み取りとローテートの間に書かれた行がそのまま消える。
      //   2世代以上が一度に流れた場合は追えないが、その欠落は seq の飛びとして
      //   クライアントに見える（?since= で埋め直せる）。
      const carried = rotated ? readCarryOver(logPath, position, inode) : [];

      if (rotated || current.size < position) position = 0;
      inode = current.inode;

      if (current.size === position) return carried;

      const { lines, consumed } = completeLines(readRange(logPath, position, current.size));
      position += consumed;

      return carried.length > 0 ? [...carried, ...lines] : lines;
    },

    backfill(since) {
      const size = fileSize(logPath);
      if (size === 0) return [];

      const chunk = readRange(logPath, 0, size);
      const lastNewline = chunk.lastIndexOf("\n");
      if (lastNewline === -1) return [];

      return chunk
        .slice(0, lastNewline)
        .split("\n")
        .filter((line) => {
          if (!line.trim()) return false;
          const seq = seqOf(line);
          return seq !== null && seq > since;
        });
    },
  };
}
