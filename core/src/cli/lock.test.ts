import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { acquireLock } from "./lock";

let dir: string;
let lockDir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-lock-"));
  lockDir = path.join(dir, "speak.lock");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

/** 確実に死んでいる pid を作る（一度使って終了した子プロセスの pid は再利用まで空く） */
const DEAD_PID = 2 ** 22 - 1;

describe("acquireLock", () => {
  it("空の状態なら取れる", () => {
    const lock = acquireLock(lockDir);
    expect(lock).not.toBeNull();
    expect(fs.existsSync(lockDir)).toBe(true);
  });

  it("先行ワーカーがいれば取れない（後続は何もせず終了する）", () => {
    const first = acquireLock(lockDir);
    expect(first).not.toBeNull();
    expect(acquireLock(lockDir)).toBeNull();
  });

  it("解放すれば次のプロセスが取れる", () => {
    acquireLock(lockDir)?.release();
    expect(fs.existsSync(lockDir)).toBe(false);
    expect(acquireLock(lockDir)).not.toBeNull();
  });

  it("二重解放しても壊れない", () => {
    const lock = acquireLock(lockDir);
    lock?.release();
    lock?.release();
    expect(fs.existsSync(lockDir)).toBe(false);
  });

  it("解放後に別プロセスが取り直したロックを、古い Lock が消さない…わけではない点に注意", () => {
    // release は「自分が消す」だけの実装。二重解放は防いでいるが、
    // 解放済みの Lock を持ち回っても他人のロックは消さない（released フラグで打ち切る）
    const first = acquireLock(lockDir);
    first?.release();
    const second = acquireLock(lockDir);
    first?.release();
    expect(fs.existsSync(lockDir)).toBe(true);
    second?.release();
  });

  it("親ディレクトリが無くても作る", () => {
    const nested = path.join(dir, "a", "b", "speak.lock");
    expect(acquireLock(nested)).not.toBeNull();
  });
});

describe("放置ロックの回収", () => {
  it("プロセスが死んでいて十分に古ければ奪う", () => {
    acquireLock(lockDir, { pid: DEAD_PID });
    const later = Date.now() + 120_000;
    expect(acquireLock(lockDir, { staleMs: 60_000, now: () => later })).not.toBeNull();
  });

  it("十分に古くてもプロセスが生きていれば奪わない（要約中の長いワーカーを殺さない）", () => {
    acquireLock(lockDir, { pid: process.pid });
    const later = Date.now() + 120_000;
    expect(acquireLock(lockDir, { staleMs: 60_000, now: () => later })).toBeNull();
  });

  it("プロセスが死んでいても新しいロックは奪わない", () => {
    acquireLock(lockDir, { pid: DEAD_PID });
    expect(acquireLock(lockDir, { staleMs: 60_000 })).toBeNull();
  });

  it("pid ファイルが無い（作成途中の）ロックは、古い場合だけ奪う", () => {
    fs.mkdirSync(lockDir, { recursive: true });
    expect(acquireLock(lockDir, { staleMs: 60_000 })).toBeNull();

    const later = Date.now() + 120_000;
    expect(acquireLock(lockDir, { staleMs: 60_000, now: () => later })).not.toBeNull();
  });

  it("pid ファイルが壊れていても、古ければ奪う", () => {
    fs.mkdirSync(lockDir, { recursive: true });
    fs.writeFileSync(path.join(lockDir, "pid"), "not-a-pid");
    const later = Date.now() + 120_000;
    expect(acquireLock(lockDir, { staleMs: 60_000, now: () => later })).not.toBeNull();
  });
});
