import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { acquireLock } from "./lock";

// ★ fs は Node の組み込みモジュールで、ESM では名前空間が configurable でないため
//   `vi.spyOn(fs, "renameSync")` は "Cannot redefine property" で落ちる。
//   `vi.mock` でモジュールごと差し替え、レースを再現したい2関数だけ vi.fn でラップする。
//   既定の実装は本物（actual）へ委譲するので、ラップしていない他のテストには影響しない
const actualFsRef = vi.hoisted(() => ({ current: null as typeof import("fs") | null }));

vi.mock("fs", async (importOriginal) => {
  const actual = await importOriginal<typeof import("fs")>();
  actualFsRef.current = actual;
  return {
    ...actual,
    renameSync: vi.fn(actual.renameSync),
    mkdirSync: vi.fn(actual.mkdirSync),
  };
});

let dir: string;
let lockDir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-lock-"));
  lockDir = path.join(dir, "speak.lock");
});

afterEach(() => {
  // ★ テストごとに mockImplementationOnce / mockImplementation で差し替えた分を、
  //   毎回「本物へ委譲する」既定の状態へ戻す。vi.fn() で作った mock には「復元すべき
  //   元の実装」という概念が無いので、vi.restoreAllMocks() では戻せない
  const actual = actualFsRef.current;
  if (actual) {
    vi.mocked(fs.renameSync).mockImplementation(actual.renameSync);
    vi.mocked(fs.mkdirSync).mockImplementation(actual.mkdirSync);
  }
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

  it("★ A-2: 印が消えている場合は消さない（自分のものと確認できないので何もしない）", () => {
    // readOwner は ENOENT も壊れた JSON も同じ null に潰す。null を「自分のもの」と
    // みなして消すと、放置ロックとして奪われた側が終了時に**新しい保持者のロック**を
    // 誤って消してしまう（相互排除そのものが消える）。owner.json が存在しない状況を
    // 直接作り、release() が lockDir に手を出さないことを見る
    const lock = acquireLock(lockDir);
    fs.rmSync(ownerPath());
    lock?.release();
    expect(fs.existsSync(lockDir)).toBe(true);
  });

  it("★ A-2: 印が壊れている場合も消さない", () => {
    const lock = acquireLock(lockDir);
    fs.writeFileSync(ownerPath(), "not-json");
    lock?.release();
    expect(fs.existsSync(lockDir)).toBe(true);
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

  it("★ A-3: 所有者が生きていれば、mtime だけが staleMs を超えても奪わない", () => {
    // mtime は mkdir 時に決まったきり更新されない（heartbeat が無い）。古さも条件に
    // 加えると、ドレインが長引いた・サスペンドから復帰しただけの**生きた保持者**から
    // 奪ってしまう。所有者が読める場合は pid の生死だけで決めること
    acquireLock(lockDir, { pid: process.pid });
    const later = Date.now() + 120_000;
    expect(acquireLock(lockDir, { staleMs: 60_000, now: () => later })).toBeNull();
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

describe("★ A-1: 奪取レース（rename の相手が入れ替わっていないか）", () => {
  it("isStale() から renameSync までの間に別プロセスが取り直していたら、その生きたロックを奪わない", () => {
    // P1 の放置ロック（死んだ pid）を作る
    acquireLock(lockDir, { pid: DEAD_PID, token: "tok-dead" });

    vi.mocked(fs.renameSync).mockImplementationOnce((from, to) => {
      // isStale() が P1 の放置ロックを見て true を返した直後、本物の renameSync に
      // 着く前に、P2 が同じ放置ロックを奪って取り直した（生きたロックになった）
      // 状況をここで作ってから、本物の rename を実行する。
      // ここでの fs.renameSync 呼び出しは once のキューが既に空なので既定（本物）に委譲される
      fs.rmSync(lockDir, { recursive: true, force: true });
      fs.mkdirSync(lockDir);
      fs.writeFileSync(ownerPath(), JSON.stringify({ pid: process.pid, token: "tok-live-p2" }));
      fs.renameSync(from, to);
    });

    const result = acquireLock(lockDir, { staleMs: 60_000, token: "tok-p3" });

    // rename 自体は lockDir が在りさえすれば成功するので、対策が無ければここで
    // P3 が P2 の生きたロックを奪って上書きしてしまう
    expect(result).toBeNull();
    expect(fs.existsSync(lockDir)).toBe(true);
    expect(ownerToken()).toBe("tok-live-p2");
  });
});

describe("★ A-4: owner.json の排他作成", () => {
  it("owner.json が既に在るのに mkdir が成功してしまっても、wx で弾かれて他人の印を壊さない", () => {
    fs.mkdirSync(lockDir, { recursive: true });
    fs.writeFileSync(ownerPath(), JSON.stringify({ pid: process.pid, token: "tok-existing" }));

    // 通常 mkdirSync は EEXIST で弾かれるので owner.json の上書きは起きない。
    // ここでは「mkdir 自体は成功したのに owner.json は既に存在する」という、
    // 奪取との競合でしか起こり得ないはずの状況を人工的に作り、wx が最後の砦に
    // なっていることを見る
    const actualMkdirSync = actualFsRef.current!.mkdirSync;
    vi.mocked(fs.mkdirSync).mockImplementation((...args: Parameters<typeof fs.mkdirSync>) => {
      const [target] = args;
      if (target === lockDir) return undefined;
      return actualMkdirSync(...args);
    });

    const result = acquireLock(lockDir, { token: "tok-new" });

    expect(result).toBeNull();
    expect(ownerToken()).toBe("tok-existing");
    expect(fs.existsSync(lockDir)).toBe(true);
  });
});
