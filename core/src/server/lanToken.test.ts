import { describe, it, expect, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { ensureServerToken } from "./lanToken";

let dir: string;
let filePath: string;

function setup(): void {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "cm-token-"));
  // ネストしたディレクトリを使う。mkdirSync(recursive) を通ることを兼ねて確かめる
  filePath = path.join(dir, "nested", "server.token");
}

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

describe("ensureServerToken", () => {
  it("無ければ生成してファイルに書く", () => {
    setup();
    const token = ensureServerToken(filePath);
    expect(token.length).toBeGreaterThan(0);
    expect(fs.readFileSync(filePath, "utf-8")).toBe(token);
  });

  it("あれば同じ値を読み直す（再生成しない）", () => {
    setup();
    const first = ensureServerToken(filePath);
    const second = ensureServerToken(filePath);
    expect(second).toBe(first);
  });

  it("生成するトークンは base64url", () => {
    setup();
    const token = ensureServerToken(filePath);
    expect(token).toMatch(/^[A-Za-z0-9_-]{43}$/);
  });

  it("パーミッションは 0600", () => {
    setup();
    ensureServerToken(filePath);
    const mode = fs.statSync(filePath).mode & 0o777;
    expect(mode).toBe(0o600);
  });

  it("空ファイルは壊れているとみなして作り直す", () => {
    setup();
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, "");
    const token = ensureServerToken(filePath);
    expect(token.length).toBeGreaterThan(0);
    expect(fs.readFileSync(filePath, "utf-8")).toBe(token);
  });

  it("空白だけのファイルも作り直す", () => {
    setup();
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, "  \n");
    const token = ensureServerToken(filePath);
    expect(token.length).toBeGreaterThan(0);
  });

  it("読める文字集合から外れた内容は作り直す", () => {
    setup();
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    fs.writeFileSync(filePath, "not-a-token!! 日本語\n");
    const token = ensureServerToken(filePath);
    expect(token).toMatch(/^[A-Za-z0-9_-]+$/);
  });

  it("2回目の呼び出しでは生成し直さない（毎回 randomBytes を呼ばない）", () => {
    setup();
    const first = ensureServerToken(filePath);
    // ファイルの mtime を書き換えず、内容だけ確認する ——
    // 生成し直していれば別の値になるはずのトークンが同じであることで代えて確認する
    for (let i = 0; i < 5; i++) {
      expect(ensureServerToken(filePath)).toBe(first);
    }
  });
});
