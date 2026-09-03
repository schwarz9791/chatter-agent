import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { readSummarizerSessions, registerSummarizerSession } from "./summarizerSessions";

let dir: string;
let filePath: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-sessions-"));
  filePath = path.join(dir, "summarizer-sessions.json");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

describe("summarizerSessions", () => {
  it("ファイルが無ければ空", () => {
    expect(readSummarizerSessions(filePath)).toEqual([]);
  });

  it("登録した session_id を読み戻せる", () => {
    registerSummarizerSession(filePath, "a");
    registerSummarizerSession(filePath, "b");
    expect(readSummarizerSessions(filePath)).toEqual(["a", "b"]);
  });

  it("上限を超えたら古い方から捨てる", () => {
    for (let i = 0; i < 20; i++) registerSummarizerSession(filePath, `s${i}`);
    const sessions = readSummarizerSessions(filePath);
    expect(sessions).toHaveLength(16);
    expect(sessions[0]).toBe("s4");
    expect(sessions.at(-1)).toBe("s19");
  });

  /** ★ 抑制リストなので、読めた分だけでも効かせる（許可リストではない） */
  it("★ 壊れた要素が混ざっていても、読めた分は返す", () => {
    fs.writeFileSync(filePath, JSON.stringify(["a", 1, null, "b"]));
    expect(readSummarizerSessions(filePath)).toEqual(["a", "b"]);
  });

  it("JSON が壊れていても throw しない（抑制が1回効かないだけ）", () => {
    fs.writeFileSync(filePath, "{ 壊れている");
    expect(readSummarizerSessions(filePath)).toEqual([]);
  });

  it("配列でなければ空", () => {
    fs.writeFileSync(filePath, JSON.stringify({ a: 1 }));
    expect(readSummarizerSessions(filePath)).toEqual([]);
  });

  /**
   * ★ 「登録できなければ CLI を起こさない」が第2層の安全側の挙動なので、
   *   書けないときは throw して呼び出し側に伝える（握らない）
   */
  it("★ 書けなければ throw する（握って黙らない）", () => {
    expect(() => registerSummarizerSession(path.join(dir, "no", "such", "dir", "s.json"), "a")).toThrow();
  });
});
