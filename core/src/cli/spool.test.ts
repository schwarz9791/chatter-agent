import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import {
  cleanOrphans,
  readMessage,
  readProgress,
  readPromptPayload,
  removeEntry,
  scanSpool,
  writeProgress,
} from "./spool";

let spoolDir: string;

beforeEach(() => {
  spoolDir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-spool-"));
});

afterEach(() => {
  fs.rmSync(spoolDir, { recursive: true, force: true });
});

/** MessageDisplay の実測ペイロード（設計書 §2-3） */
function delta(index: number, text: string, final = false, messageId = "m1") {
  return {
    session_id: "sess-1",
    transcript_path: "/tmp/x.jsonl",
    cwd: "/tmp",
    prompt_id: "p1",
    hook_event_name: "MessageDisplay",
    turn_id: "turn-1",
    message_id: messageId,
    index,
    final,
    delta: text,
  };
}

function writeMessage(messageId: string, payloads: unknown[]): string {
  const filePath = path.join(spoolDir, `${messageId}.jsonl`);
  fs.writeFileSync(filePath, payloads.map((p) => JSON.stringify(p)).join("\n") + "\n");
  return filePath;
}

/**
 * 到着順のテストで、ファイルの作成時刻を確実にずらす。
 *
 * 実装はナノ秒まで見るので通常はずれるが、タイムスタンプの粒度が粗いファイルシステムでも
 * 落ちないよう、明示的に間隔を空ける。作成順そのものを検証したいので、
 * birthtime を後から書き換える手は使えない（utimes は mtime しか動かせない）。
 */
function tick(ms = 2): void {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

function setBirthtime(filePath: string, ms: number): void {
  // birthtime は直接いじれないので、mtime を落として fallback 経路も含めて順序を作る。
  // macOS/APFS では作成順がそのまま birthtime になるため、テストは作成順で担保する。
  const t = new Date(ms);
  fs.utimesSync(filePath, t, t);
}

describe("scanSpool", () => {
  it("ディレクトリが無くても落ちない", () => {
    expect(scanSpool(path.join(spoolDir, "nope"))).toEqual([]);
  });

  it("メッセージと応答待ちを種別で分ける", () => {
    writeMessage("m1", [delta(0, "あ。")]);
    fs.writeFileSync(path.join(spoolDir, "prompt-abc.json"), "{}");

    const entries = scanSpool(spoolDir);
    expect(entries.map((e) => e.kind).sort()).toEqual(["message", "prompt"]);
  });

  it("進捗サイドカーは走査対象にしない（prompt-*.json のグロブに混ざらせない）", () => {
    writeMessage("m1", [delta(0, "あ。")]);
    writeProgress(path.join(spoolDir, "m1.progress.json"), 1);
    // prompt- で始まるメッセージのサイドカーも弾けること
    writeProgress(path.join(spoolDir, "prompt-x.progress.json"), 1);

    const entries = scanSpool(spoolDir);
    expect(entries).toHaveLength(1);
    expect(entries[0]?.kind).toBe("message");
  });

  it("関係ないファイルは無視する", () => {
    fs.writeFileSync(path.join(spoolDir, "README.txt"), "x");
    fs.mkdirSync(path.join(spoolDir, "subdir"));
    expect(scanSpool(spoolDir)).toEqual([]);
  });

  it("メッセージのファイル名から message_id とサイドカーのパスを決める", () => {
    writeMessage("a94cd2c5", [delta(0, "あ。")]);
    const entry = scanSpool(spoolDir)[0];
    expect(entry?.kind).toBe("message");
    if (entry?.kind !== "message") throw new Error("unreachable");
    expect(entry.messageId).toBe("a94cd2c5");
    expect(entry.progressPath).toBe(path.join(spoolDir, "a94cd2c5.progress.json"));
  });

  it("到着順（作成順）に並ぶ", () => {
    // 名前順に並べると first → prompt-third → second になるので、
    // 作成順で並んでいることがこの並びで分かる
    writeMessage("first", [delta(0, "あ。")]);
    tick();
    writeMessage("second", [delta(0, "い。")]);
    tick();
    fs.writeFileSync(path.join(spoolDir, "prompt-third.json"), "{}");

    expect(scanSpool(spoolDir).map((e) => path.basename(e.filePath))).toEqual([
      "first.jsonl",
      "second.jsonl",
      "prompt-third.json",
    ]);
  });

  it("先に始まったメッセージが後から追記されても順序が入れ替わらない", () => {
    // ★ mtime で並べるとここが壊れる。final:true は 34〜80 秒遅れて届くため、
    //   先行メッセージの mtime が後発より新しくなるのが普通に起きる
    const first = writeMessage("first", [delta(0, "あ。")]);
    tick();
    writeMessage("second", [delta(0, "い。")]);

    fs.appendFileSync(first, JSON.stringify(delta(1, "う。", true)) + "\n");
    setBirthtime(first, Date.now() + 60_000);

    expect(scanSpool(spoolDir).map((e) => path.basename(e.filePath))).toEqual(["first.jsonl", "second.jsonl"]);
  });
});

describe("readMessage", () => {
  it("index 順に delta を並べる", () => {
    const filePath = writeMessage("m1", [delta(1, "います。"), delta(0, "確認して")]);
    const content = readMessage(filePath);
    expect(content.deltas).toEqual(["確認して", "います。"]);
    expect(content.final).toBe(false);
  });

  it("final:true を拾う", () => {
    const filePath = writeMessage("m1", [delta(0, "あ。"), delta(1, "い。", true)]);
    expect(readMessage(filePath).final).toBe(true);
  });

  it("payload から sessionId / turnId / messageId を取る", () => {
    const filePath = writeMessage("m1", [delta(0, "あ。")]);
    const content = readMessage(filePath);
    expect(content.sessionId).toBe("sess-1");
    expect(content.turnId).toBe("turn-1");
    expect(content.messageId).toBe("m1");
  });

  it("index に欠番があればそこで打ち切る（歯抜けを繋いで文を壊さない）", () => {
    const filePath = writeMessage("m1", [delta(0, "あ。"), delta(2, "う。", true)]);
    const content = readMessage(filePath);
    expect(content.deltas).toEqual(["あ。"]);
    expect(content.final).toBe(false);
  });

  it("index 0 が無ければ何も読まない", () => {
    const filePath = writeMessage("m1", [delta(1, "い。")]);
    expect(readMessage(filePath).deltas).toEqual([]);
  });

  it("途中で切れた行は捨てて、読めた行だけ使う", () => {
    const filePath = path.join(spoolDir, "m1.jsonl");
    fs.writeFileSync(filePath, JSON.stringify(delta(0, "あ。")) + "\n" + '{"index":1,"delta":"い');
    expect(readMessage(filePath).deltas).toEqual(["あ。"]);
  });

  it("同じ index が二度来たら後勝ち", () => {
    const filePath = writeMessage("m1", [delta(0, "旧"), delta(0, "新")]);
    expect(readMessage(filePath).deltas).toEqual(["新"]);
  });

  it("ファイルが無ければ空", () => {
    expect(readMessage(path.join(spoolDir, "nope.jsonl")).deltas).toEqual([]);
  });
});

describe("readPromptPayload", () => {
  it("payload をそのまま返す", () => {
    const filePath = path.join(spoolDir, "prompt-a.json");
    fs.writeFileSync(filePath, JSON.stringify({ hook_event_name: "Notification", message: "許可して" }));
    expect(readPromptPayload(filePath)).toEqual({ hook_event_name: "Notification", message: "許可して" });
  });

  it("壊れていれば null", () => {
    const filePath = path.join(spoolDir, "prompt-a.json");
    fs.writeFileSync(filePath, "{ broken");
    expect(readPromptPayload(filePath)).toBeNull();
  });
});

describe("進捗サイドカー", () => {
  it("書いて読める", () => {
    const p = path.join(spoolDir, "m1.progress.json");
    writeProgress(p, 3);
    expect(readProgress(p)).toBe(3);
  });

  it("無ければ 0", () => {
    expect(readProgress(path.join(spoolDir, "nope.progress.json"))).toBe(0);
  });

  it("壊れていれば 0（多少喋り直すが、黙るよりは良い）", () => {
    const p = path.join(spoolDir, "m1.progress.json");
    fs.writeFileSync(p, "{ emitted");
    expect(readProgress(p)).toBe(0);
  });

  it("★ 書き込みが途中で落ちても 0 バイトのサイドカーを残さない", () => {
    // writeFileSync は O_TRUNC してから書くので、素で使うとその隙に落ちたときに
    // 進捗が 0 に化け、メッセージが丸ごと読み直される
    const p = path.join(spoolDir, "m1.progress.json");
    writeProgress(p, 2);
    writeProgress(p, 5);

    expect(readProgress(p)).toBe(5);
    // 一時ファイルを残さない
    expect(fs.readdirSync(spoolDir).filter((f) => f.endsWith(".tmp"))).toEqual([]);
  });

  it("一時ファイルは走査対象にならない", () => {
    writeMessage("m1", [delta(0, "あ。")]);
    fs.writeFileSync(path.join(spoolDir, "m1.progress.json.tmp"), "{}");

    expect(scanSpool(spoolDir)).toHaveLength(1);
  });
});

describe("removeEntry", () => {
  it("メッセージはサイドカーごと消す", () => {
    writeMessage("m1", [delta(0, "あ。")]);
    const entry = scanSpool(spoolDir)[0]!;
    if (entry.kind !== "message") throw new Error("unreachable");
    writeProgress(entry.progressPath, 1);

    removeEntry(entry);
    expect(fs.readdirSync(spoolDir)).toEqual([]);
  });

  it("応答待ちはそのファイルだけ消す", () => {
    fs.writeFileSync(path.join(spoolDir, "prompt-a.json"), "{}");
    removeEntry(scanSpool(spoolDir)[0]!);
    expect(fs.readdirSync(spoolDir)).toEqual([]);
  });
});

describe("cleanOrphans", () => {
  it("無活動が閾値を超えたファイルを消す", () => {
    const stale = writeMessage("stale", [delta(0, "あ。")]);
    writeMessage("fresh", [delta(0, "い。")]);
    setBirthtime(stale, Date.now() - 10 * 60 * 60 * 1000);

    expect(cleanOrphans(spoolDir, 6 * 60 * 60 * 1000)).toBe(1);
    expect(fs.readdirSync(spoolDir)).toEqual(["fresh.jsonl"]);
  });

  it("進行中のメッセージは消さない（delta のたびに mtime が更新される）", () => {
    writeMessage("m1", [delta(0, "あ。")]);
    expect(cleanOrphans(spoolDir, 6 * 60 * 60 * 1000)).toBe(0);
  });

  it("ディレクトリが無くても落ちない", () => {
    expect(cleanOrphans(path.join(spoolDir, "nope"), 1000)).toBe(0);
  });
});
