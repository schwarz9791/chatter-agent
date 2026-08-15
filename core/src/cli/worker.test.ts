import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createSpeechLog } from "../core/speechLog";
import type { SpeechRecord } from "../core/types";
import { drainSpool } from "./worker";
import type { DrainDeps } from "./worker";

let dir: string;
let spoolDir: string;
let logPath: string;

const HOUR = 60 * 60 * 1000;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-worker-"));
  spoolDir = path.join(dir, "spool");
  logPath = path.join(dir, "speech.jsonl");
  fs.mkdirSync(spoolDir);
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

let clock = Date.parse("2026-08-15T00:00:00.000Z");

function drain(overrides: Partial<DrainDeps> = {}) {
  return drainSpool({
    spoolDir,
    speechLog: createSpeechLog({
      logPath,
      statePath: path.join(dir, "speech.state.json"),
      maxBytes: 1024 * 1024,
      generations: 3,
      now: () => new Date(clock),
    }),
    workerStatePath: path.join(dir, "speak.state.json"),
    speakPrompts: true,
    spoolMaxAgeMs: 6 * HOUR,
    classify: () => "neutral",
    now: () => clock,
    ...overrides,
  });
}

function appendDelta(messageId: string, index: number, text: string, final = false): void {
  fs.appendFileSync(
    path.join(spoolDir, `${messageId}.jsonl`),
    JSON.stringify({
      session_id: "sess-1",
      hook_event_name: "MessageDisplay",
      turn_id: "turn-1",
      message_id: messageId,
      index,
      final,
      delta: text,
    }) + "\n",
  );
}

function writePrompt(name: string, payload: unknown): void {
  fs.writeFileSync(path.join(spoolDir, `prompt-${name}.json`), JSON.stringify(payload));
}

function records(): SpeechRecord[] {
  if (!fs.existsSync(logPath)) return [];
  return fs
    .readFileSync(logPath, "utf-8")
    .split("\n")
    .filter((l) => l.trim())
    .map((l) => JSON.parse(l) as SpeechRecord);
}

const texts = () => records().map((r) => r.text);

describe("メッセージの処理", () => {
  it("確定した文だけを書き、最後の文は保留する", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    expect(drain().written).toBe(1);
    expect(texts()).toEqual(["確認します。"]);
  });

  it("final:true が来るまで spool を消さない", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();
    expect(fs.existsSync(path.join(spoolDir, "m1.jsonl"))).toBe(true);
    expect(fs.existsSync(path.join(spoolDir, "m1.progress.json"))).toBe(true);
  });

  it("final:true を処理し終えたら spool をサイドカーごと消す", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。", true);
    drain();
    expect(fs.readdirSync(spoolDir)).toEqual([]);
    expect(texts()).toEqual(["確認します。", "ログを見ます。"]);
  });

  it("delta を跨いでも同じ文を二度書かない", () => {
    appendDelta("m1", 0, "あ。い");
    drain();
    appendDelta("m1", 1, "。う。");
    drain();
    appendDelta("m1", 2, "え。", true);
    drain();
    expect(texts()).toEqual(["あ。", "い。", "う。", "え。"]);
  });

  it("契約どおりのフィールドで書く", () => {
    appendDelta("m1", 0, "確認します。", true);
    drain({ classify: () => "happy" });
    expect(records()[0]).toMatchObject({
      seq: 1,
      source: "claude-code",
      sessionId: "sess-1",
      turnId: "turn-1",
      messageId: "m1",
      kind: "assistant",
      text: "確認します。",
      emotion: "happy",
    });
  });
});

describe("後続イベントによる保留解除（設計書からの上積み）", () => {
  it("別メッセージが始まったら、前メッセージの最後の文を先に流す", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();
    expect(texts()).toEqual(["確認します。"]);

    appendDelta("m2", 0, "次に進みます。");
    drain();
    // m1 の保留分が先。m2 は最後の文なのでまだ保留
    expect(texts()).toEqual(["確認します。", "ログを見ます。"]);
  });

  it("応答待ち通知の到着でも保留を解除する（ツールが始まる＝メッセージは閉じた）", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();

    writePrompt("p1", {
      session_id: "sess-1",
      prompt_id: "p1",
      hook_event_name: "Notification",
      message: "許可してください。",
    });
    drain();

    expect(texts()).toEqual(["確認します。", "ログを見ます。", "許可してください。"]);
  });

  it("先に流した文を、遅れて届いた final で再送しない", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();
    appendDelta("m2", 0, "次に進みます。");
    drain();

    appendDelta("m1", 1, "", true); // 80 秒遅れの final（中身は無し）
    drain();

    expect(texts().filter((t) => t === "ログを見ます。")).toHaveLength(1);
  });
});

describe("応答待ち通知", () => {
  const question = {
    session_id: "sess-1",
    prompt_id: "p9",
    hook_event_name: "PreToolUse",
    tool_name: "AskUserQuestion",
    tool_input: {
      questions: [{ question: "次は何をしますか？", options: [{ label: "進める" }, { label: "やめる" }] }],
    },
  };

  it("質問と選択肢を1文1行で書く", () => {
    writePrompt("q", question);
    drain();
    expect(texts()).toEqual(["次は何をしますか？", "選択肢は、進める、やめる。"]);
    expect(records()[0]?.kind).toBe("prompt");
  });

  it("PreToolUse に付随する同一 prompt_id の Notification は捨てる", () => {
    writePrompt("q", question);
    drain();
    writePrompt("n", { session_id: "sess-1", prompt_id: "p9", hook_event_name: "Notification", message: "許可を。" });
    drain();
    expect(texts()).not.toContain("許可を。");
  });

  it("抑制は1回だけ効く（同じターンの2つ目の許可プロンプトは読む）", () => {
    writePrompt("q", question);
    drain();
    writePrompt("n1", { session_id: "sess-1", prompt_id: "p9", hook_event_name: "Notification", message: "1回目。" });
    drain();
    clock += 5_000; // 連投抑制の 3 秒窓は外す
    writePrompt("n2", { session_id: "sess-1", prompt_id: "p9", hook_event_name: "Notification", message: "2回目。" });
    drain();
    expect(texts()).toContain("2回目。");
  });

  it("10 秒の窓を過ぎた Notification は捨てない", () => {
    writePrompt("q", question);
    drain();
    clock += 20_000;
    writePrompt("n", { session_id: "sess-1", prompt_id: "p9", hook_event_name: "Notification", message: "許可を。" });
    drain();
    expect(texts()).toContain("許可を。");
  });

  it("同じ文面の連投は 3 秒窓で抑制する", () => {
    const notify = { session_id: "sess-1", hook_event_name: "Notification", message: "許可を。" };
    writePrompt("n1", notify);
    drain();
    writePrompt("n2", notify);
    drain();
    expect(texts().filter((t) => t === "許可を。")).toHaveLength(1);
  });

  it("speakPrompts が false なら書かないが spool は消す", () => {
    writePrompt("q", question);
    drain({ speakPrompts: false });
    expect(texts()).toEqual([]);
    expect(fs.readdirSync(spoolDir)).toEqual([]);
  });

  it("壊れた payload でも spool を消して先へ進む", () => {
    fs.writeFileSync(path.join(spoolDir, "prompt-broken.json"), "{ broken");
    drain();
    expect(fs.readdirSync(spoolDir)).toEqual([]);
  });

  it("抑制状態はプロセスを跨いで効く（CLI は毎 delta 起動して終了する）", () => {
    writePrompt("q", question);
    drain();
    // drain() のたびに state を読み直しているので、別プロセスと同じ条件
    expect(fs.existsSync(path.join(dir, "speak.state.json"))).toBe(true);
  });
});

describe("ドレインの停止条件", () => {
  it("到着待ちだけが残っていれば空振りで止まる", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    const result = drain();
    expect(result.passes).toBeLessThanOrEqual(3);
  });

  it("spool が空なら1周で止まる", () => {
    expect(drain().passes).toBe(0);
  });
});

describe("孤児の掃除", () => {
  it("無活動が閾値を超えた spool を消す", () => {
    appendDelta("old", 0, "あ。");
    const stale = new Date(clock - 10 * HOUR);
    fs.utimesSync(path.join(spoolDir, "old.jsonl"), stale, stale);

    expect(drain().orphansRemoved).toBe(1);
    expect(fs.readdirSync(spoolDir)).toEqual([]);
  });

  it("進行中のものは消さない", () => {
    appendDelta("m1", 0, "あ。");
    expect(drain().orphansRemoved).toBe(0);
  });
});
