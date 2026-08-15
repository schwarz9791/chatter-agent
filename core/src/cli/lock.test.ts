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

/** 確実に死んでいる pid（PID_MAX を超えるので割り当てられない） */
const DEAD_PID = 2 ** 22 - 1;

const ownerPath = () => path.join(lockDir, "owner.json");
const ownerToken = () => (JSON.parse(fs.readFileSync(ownerPath(), "utf-8")) as { token: string }).token;

describe("acquireLock", () => {
  it("空の状態なら取れる", () => {
    expect(acquireLock(lockDir)).not.toBeNull();
    expect(fs.existsSync(lockDir)).toBe(true);
  });

  it("先行ワーカーがいれば取れない（後続は何もせず終了する）", () => {
    expect(acquireLock(lockDir)).not.toBeNull();
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

  it("親ディレクトリが無くても作る", () => {
    expect(acquireLock(path.join(dir, "a", "b", "speak.lock"))).not.toBeNull();
  });

  it("取得したプロセスの所有印を残す", () => {
    acquireLock(lockDir, { pid: 4242, token: "tok-a" });
    expect(JSON.parse(fs.readFileSync(ownerPath(), "utf-8"))).toEqual({ pid: 4242, token: "tok-a" });
  });
});

describe("所有権（★ 奪われた側が他人のロックを消さないこと）", () => {
  it("奪われた後に release しても、新しい保持者のロックを消さない", () => {
    const evicted = acquireLock(lockDir, { token: "tok-old" });
    expect(evicted).not.toBeNull();

    // 別プロセスが放置ロックとして奪い、自分の印を書いた状態を作る
    fs.writeFileSync(ownerPath(), JSON.stringify({ pid: process.pid, token: "tok-new" }));

    evicted?.release();

    expect(fs.existsSync(lockDir)).toBe(true);
    expect(ownerToken()).toBe("tok-new");
  });

  it("自分の印が残っていれば消す", () => {
    const lock = acquireLock(lockDir, { token: "tok-mine" });
    lock?.release();
    expect(fs.existsSync(lockDir)).toBe(false);
  });

  it("印が読めない（書けなかった）場合は消す", () => {
    const lock = acquireLock(lockDir);
    fs.rmSync(ownerPath());
    lock?.release();
    expect(fs.existsSync(lockDir)).toBe(false);
  });
});

describe("放置ロックの回収", () => {
  it("★ プロセスが死んでいれば、古くなるのを待たずに奪う", () => {
    // 経過時間を先に見ていると、SIGKILL / SIGHUP のあと staleMs まるごと無言になる
    acquireLock(lockDir, { pid: DEAD_PID });
    expect(acquireLock(lockDir, { staleMs: 60_000 })).not.toBeNull();
  });

  it("プロセスが生きていれば staleMs までは奪わない", () => {
    acquireLock(lockDir, { pid: process.pid });
    expect(acquireLock(lockDir, { staleMs: 60_000 })).toBeNull();
  });

  it("生きて見えても staleMs を超えたら奪う（pid 再利用への backstop）", () => {
    acquireLock(lockDir, { pid: process.pid });
    const later = Date.now() + 120_000;
    expect(acquireLock(lockDir, { staleMs: 60_000, now: () => later })).not.toBeNull();
  });

  it("印が無い（作成途中の）ロックは、古い場合だけ奪う", () => {
    fs.mkdirSync(lockDir, { recursive: true });
    expect(acquireLock(lockDir, { staleMs: 60_000 })).toBeNull();

    const later = Date.now() + 120_000;
    expect(acquireLock(lockDir, { staleMs: 60_000, now: () => later })).not.toBeNull();
  });

  it("印が壊れていても、古ければ奪う", () => {
    fs.mkdirSync(lockDir, { recursive: true });
    fs.writeFileSync(ownerPath(), "not-json");
    const later = Date.now() + 120_000;
    expect(acquireLock(lockDir, { staleMs: 60_000, now: () => later })).not.toBeNull();
  });

  it("奪った後は自分の印だけが残り、退避したディレクトリも片付いている", () => {
    acquireLock(lockDir, { pid: DEAD_PID, token: "tok-dead" });
    acquireLock(lockDir, { staleMs: 60_000, token: "tok-live" });

    expect(ownerToken()).toBe("tok-live");
    expect(fs.readdirSync(dir).filter((f) => f.includes("evicted"))).toEqual([]);
  });
});
