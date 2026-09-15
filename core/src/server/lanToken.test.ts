import { describe, it, expect, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { ensureServerToken } from "./lanToken";

// ★ fs は Node の組み込みモジュールで、ESM では名前空間が configurable でないため
//   `vi.spyOn(fs, "readFileSync")` は "Cannot redefine property" で落ちる（→ core/lock.test.ts）。
//   `vi.mock` でモジュールごと差し替え、ENOENT 以外の読み取りエラーを再現したい1関数だけ
//   vi.fn でラップする。既定の実装は本物（actual）へ委譲するので、他のテストには影響しない
const actualFsRef = vi.hoisted(() => ({ current: null as typeof import("fs") | null }));

vi.mock("fs", async (importOriginal) => {
  const actual = await importOriginal<typeof import("fs")>();
  actualFsRef.current = actual;
  return {
    ...actual,
    readFileSync: vi.fn(actual.readFileSync),
  };
});

let dir: string;
let filePath: string;

function setup(): void {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "cm-token-"));
  // ネストしたディレクトリを使う。mkdirSync(recursive) を通ることを兼ねて確かめる
  filePath = path.join(dir, "nested", "server.token");
}

afterEach(() => {
  // 差し替えた分を、毎回「本物へ委譲する」既定の状態へ戻す（→ core/lock.test.ts と同じ理由）
  const actual = actualFsRef.current;
  if (actual) vi.mocked(fs.readFileSync).mockImplementation(actual.readFileSync);
  fs.rmSync(dir, { recursive: true, force: true });
});

describe("ensureServerToken", () => {
  it("無ければ生成してファイルに書き、created は new", () => {
    setup();
    const { token, created } = ensureServerToken(filePath);
    expect(token.length).toBeGreaterThan(0);
    expect(fs.readFileSync(filePath, "utf-8")).toBe(token);
    expect(created).toBe("new");
  });

  it("あれば同じ値を読み直し、created は null（再生成しない）", () => {
    setup();
    const first = ensureServerToken(filePath);
    const second = ensureServerToken(filePath);
    expect(second.token).toBe(first.token);
    expect(second.created).toBeNull();
  });

  it("生成するトークンは base64url", () => {
    setup();
    const { token } = ensureServerToken(filePath);
    expect(token).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it("パーミッションは 0600", () => {
    setup();
    ensureServerToken(filePath);
    const mode = fs.statSync(filePath).mode & 0o777;
    expect(mode).toBe(0o600);
  });

  it("空ファイルは壊れているとみなして作り直し、created は replaced", () => {
    setup();
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, "");
    const { token, created } = ensureServerToken(filePath);
    expect(token.length).toBeGreaterThan(0);
    expect(fs.readFileSync(filePath, "utf-8")).toBe(token);
    expect(created).toBe("replaced");
  });

  it("空白だけのファイルも作り直す", () => {
    setup();
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, "  \n");
    const { token, created } = ensureServerToken(filePath);
    expect(token.length).toBeGreaterThan(0);
    expect(created).toBe("replaced");
  });

  it("読める文字集合から外れた内容は作り直す", () => {
    setup();
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, "not-a-token!! 日本語\n");
    const { token, created } = ensureServerToken(filePath);
    expect(token).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(created).toBe("replaced");
  });

  it("読み取りエラー（ENOENT 以外）も作り直し扱いになる", () => {
    setup();
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, "irrelevant");
    // EACCES など、ENOENT 以外の読み取りエラーを模す
    const eacces = Object.assign(new Error("EACCES"), { code: "EACCES" });
    vi.mocked(fs.readFileSync).mockImplementationOnce(() => {
      throw eacces;
    });
    const { token, created } = ensureServerToken(filePath);
    expect(token).toMatch(/^[A-Za-z0-9_-]+$/);
    expect(created).toBe("replaced");
  });

  it("2回目の呼び出しでは生成し直さない（毎回 randomBytes を呼ばない）", () => {
    setup();
    const first = ensureServerToken(filePath);
    // ファイルの mtime を書き換えず、内容だけ確認する ——
    // 生成し直していれば別の値になるはずのトークンが同じであることで代えて確認する
    for (let i = 0; i < 5; i++) {
      expect(ensureServerToken(filePath).token).toBe(first.token);
    }
  });
});
