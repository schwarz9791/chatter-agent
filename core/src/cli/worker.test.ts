import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { acquireLock } from "../core/lock";
import { createSpeechLog } from "../core/speechLog";
import { createSpeechQueue } from "../core/speechQueue";
import type { SpeechRecord } from "../core/types";
import { scanSpool } from "./spool";
import { acquireLockWithRetry, drainSpool } from "./worker";
import type { DrainDeps } from "./worker";

// ★ scanSpool は「進展の無いパスの直後に届いた spool」を再現するために差し替える。
//   モジュールごと差し替え、scanSpool だけ vi.fn でラップする（./spool.ts の他の
//   エクスポートは実体のまま素通りさせる）。パターンは core/lock.test.ts と同じ
const actualSpoolRef = vi.hoisted(() => ({ current: null as typeof import("./spool") | null }));

vi.mock("./spool", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./spool")>();
  actualSpoolRef.current = actual;
  return {
    ...actual,
    scanSpool: vi.fn(actual.scanSpool),
  };
});

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
  // テストごとに mockImplementationOnce で差し替えた分を、毎回「本物へ委譲する」既定の
  // 状態へ戻す（core/lock.test.ts と同じ理由）
  const actual = actualSpoolRef.current;
  if (actual) vi.mocked(scanSpool).mockImplementation(actual.scanSpool);
  fs.rmSync(dir, { recursive: true, force: true });
});

let clock = Date.parse("2026-08-15T00:00:00.000Z");

function drain(overrides: Partial<DrainDeps> = {}) {
  const speechLog = createSpeechLog({
    logPath,
    statePath: path.join(dir, "speech.state.json"),
    maxBytes: 1024 * 1024,
    now: () => new Date(clock),
  });
  const speechQueue = createSpeechQueue(path.join(dir, "speech"));

  return drainSpool({
    spoolDir,
    // 本番と同じく、記録と配信キューの両方に書く
    publish: (entries) => {
      const records = speechLog.append(entries);
      speechQueue.enqueue(records);
      return records;
    },
    workerStatePath: path.join(dir, "speak.state.json"),
    speakPrompts: true,
    spoolMaxAgeMs: 6 * HOUR,
    classify: () => "neutral",
    now: () => clock,
    ...overrides,
  });
}

/**
 * spool に delta を1本置く（plugin の bash hook が `<message_id>.<index>.json` を
 * tmp + rename で置くのと同じ結果になればよいので、テストでは直接 writeFileSync でよい）。
 */
function appendDelta(messageId: string, index: number, text: string, final = false, sessionId = "sess-1"): void {
  fs.writeFileSync(
    path.join(spoolDir, `${messageId}.${index}.json`),
    JSON.stringify({
      session_id: sessionId,
      hook_event_name: "MessageDisplay",
      turn_id: "turn-1",
      message_id: messageId,
      index,
      final,
      delta: text,
    }),
  );
}

/**
 * 到着順を確実にずらす。`scanSpool` は birthtime をナノ秒で見るので通常はずれるが、
 * タイムスタンプの粒度が粗いファイルシステムでも落ちないよう明示的に間隔を空ける。
 */
function tick(ms = 2): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
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
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(true);
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

    tick();
    appendDelta("m2", 0, "次に進みます。");
    drain();
    // m1 の保留分が先。m2 は最後の文なのでまだ保留
    expect(texts()).toEqual(["確認します。", "ログを見ます。"]);
  });

  it("★ 別セッションのメッセージでは保留を解かない", () => {
    // spool はグローバルに1ディレクトリで、MessageDisplay は matcher 非対応なので
    // 全セッションで発火する。Claude Code を2枚開くだけでこの条件が揃う
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();
    expect(texts()).toEqual(["確認します。"]);

    tick();
    appendDelta("other", 0, "別セッションです。", false, "sess-2");
    drain();

    expect(texts()).toEqual(["確認します。"]);
  });

  it("★ 文の途中で切れていれば、後続イベントが来ても流さない", () => {
    appendDelta("m1", 0, "Aの一文目。Aの途中");
    drain();
    expect(texts()).toEqual(["Aの一文目。"]);

    tick();
    appendDelta("m2", 0, "Bの一文目。Bの二文目。");
    drain();

    // 「Aの途中」を読み上げてしまうと、断片が音声になり順序も壊れる
    expect(texts()).not.toContain("Aの途中");

    tick();
    appendDelta("m1", 1, "です。Aの最後の文です。", true);
    drain();

    expect(texts()).toEqual(["Aの一文目。", "Bの一文目。", "Aの途中です。", "Aの最後の文です。"]);
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
    tick();
    appendDelta("m2", 0, "次に進みます。");
    drain();

    appendDelta("m1", 1, "", true); // 大きく遅れて届く final（中身は無し）
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

  it("★ 読めない payload は消さずに残す（書き込み途中を掴んだだけかもしれない）", () => {
    const broken = path.join(spoolDir, "prompt-broken.json");
    fs.writeFileSync(broken, '{"hook_event_name":"PreToolUse","tool_na');
    drain();

    expect(fs.existsSync(broken)).toBe(true);
    expect(texts()).toEqual([]);

    // 書き終われば次のドレインで拾える
    fs.writeFileSync(broken, JSON.stringify(question));
    drain();
    expect(texts()).toEqual(["次は何をしますか？", "選択肢は、進める、やめる。"]);
    expect(fs.existsSync(broken)).toBe(false);
  });

  it("★ 書き込みに失敗したら spool を消さない（イベントを復旧不能に失わない）", () => {
    writePrompt("q", question);

    const failing = () => {
      throw new Error("ENOSPC");
    };
    expect(() => drain({ publish: failing })).toThrow("ENOSPC");
    expect(fs.existsSync(path.join(spoolDir, "prompt-q.json"))).toBe(true);
  });

  it("★ 抑制状態はプロセスを跨いで効く（CLI は毎 delta 起動して終了する）", () => {
    // drain() のたびに state をディスクから読み直しているので、別プロセスと同じ条件
    writePrompt("q", question);
    drain();
    expect(texts()).toEqual(["次は何をしますか？", "選択肢は、進める、やめる。"]);

    writePrompt("n", { session_id: "sess-1", prompt_id: "p9", hook_event_name: "Notification", message: "許可を。" });
    drain();

    // 直前のドレインが書いた speak.state.json を読めていなければ、ここで抑制が効かない
    expect(texts()).not.toContain("許可を。");
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

  it("★ !changed で終わるパスの直後に届いた spool も、同じドレインで拾う", () => {
    // 句点が無く、まだ確定しない断片を1件だけ置く。処理してもファイルは残り written も
    // 0 なので、このパスは !changed で終わる（CLAUDE.md「絶対に守ること」4）
    appendDelta("m1", 0, "確認します");

    // 1回目の scanSpool が返った直後（＝そのパスの走査が終わった直後）に、別の spool が
    // 届いた状況を作る。mockImplementationOnce なので効くのは最初の1回だけで、以降は本物
    vi.mocked(scanSpool).mockImplementationOnce((dir) => {
      const result = actualSpoolRef.current!.scanSpool(dir);
      writePrompt("late", { session_id: "sess-1", hook_event_name: "Notification", message: "遅れて届いた通知。" });
      return result;
    });

    const result = drain();

    // 旧実装（1回目の !changed で即 break）だと、この spool は次にどこかの hook が
    // 発火するまで誰にも拾われない。2回連続の空振りではじめて抜けるようにすると、
    // 同じドレインの中でもう一度走査が走り、ここで拾われる
    expect(texts()).toContain("遅れて届いた通知。");
    expect(result.passes).toBeGreaterThanOrEqual(2);
  });
});

describe("孤児の掃除", () => {
  it("無活動が閾値を超えた spool を消す", () => {
    appendDelta("old", 0, "あ。");
    const stale = new Date(clock - 10 * HOUR);
    fs.utimesSync(path.join(spoolDir, "old.0.json"), stale, stale);

    expect(drain().orphansRemoved).toBe(1);
    expect(fs.readdirSync(spoolDir)).toEqual([]);
  });

  it("進行中のものは消さない", () => {
    appendDelta("m1", 0, "あ。");
    expect(drain().orphansRemoved).toBe(0);
  });
});

describe("acquireLockWithRetry", () => {
  it("★ 先行 worker がロックを長く保持していても、予算内なら解放を待って取得できる", () => {
    const lockDir = path.join(dir, "speak.lock");
    const held = acquireLock(lockDir);
    expect(held).not.toBeNull();

    // 実時間を待つと遅いので擬似クロックで駆動する。旧予算（4回試行 × 120ms ≒ 360ms、
    // Node 起動込みの実測は408〜420ms）を大きく超える600msの保持を再現する
    let clock = 0;
    let sleepCalls = 0;
    const acquired = acquireLockWithRetry(lockDir, {
      now: () => clock,
      sleep: () => {
        sleepCalls++;
        clock += 120;
        if (clock >= 600) held!.release(); // 先行 worker がここでようやく解放する
      },
    });

    expect(acquired).not.toBeNull();
    // 旧 LOCK_RETRIES=3 の予算（sleep は最大3回）では届かない待ち時間であることの確認
    expect(sleepCalls).toBeGreaterThan(3);
    acquired?.release();
  });
});
