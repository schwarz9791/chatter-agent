import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createSpeechLog } from "./speechLog";
import type { SpeechEntry, SpeechLogDeps } from "./speechLog";
import type { SpeechRecord } from "./types";

let dir: string;
let logPath: string;
let statePath: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-speechlog-"));
  logPath = path.join(dir, "speech.jsonl");
  statePath = path.join(dir, "speech.state.json");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

function log(overrides: Partial<SpeechLogDeps> = {}) {
  return createSpeechLog({
    logPath,
    statePath,
    maxBytes: 1024 * 1024,
    now: () => new Date("2026-08-15T00:00:00.000Z"),
    ...overrides,
  });
}

function entry(text: string): SpeechEntry {
  return {
    source: "claude-code",
    sessionId: "s1",
    turnId: "t1",
    messageId: "m1",
    kind: "assistant",
    text,
    emotion: "neutral",
  };
}

function readLines(filePath = logPath): SpeechRecord[] {
  return fs
    .readFileSync(filePath, "utf-8")
    .split("\n")
    .filter((line) => line.trim())
    .map((line) => JSON.parse(line) as SpeechRecord);
}

describe("append", () => {
  it("1文1行で追記し、seq を1から採番する", () => {
    const l = log();
    const records = l.append([entry("あ。"), entry("い。")]);

    expect(records.map((r) => r.seq)).toEqual([1, 2]);
    expect(readLines().map((r) => r.text)).toEqual(["あ。", "い。"]);
  });

  it("契約どおりのフィールドを持つ", () => {
    const l = log();
    l.append([entry("確認します。")]);
    expect(readLines()[0]).toEqual({
      epoch: l.epoch,
      seq: 1,
      ts: "2026-08-15T00:00:00.000Z",
      source: "claude-code",
      sessionId: "s1",
      turnId: "t1",
      messageId: "m1",
      kind: "assistant",
      text: "確認します。",
      emotion: "neutral",
    });
  });

  it("空配列では何も書かない", () => {
    const l = log();
    expect(l.append([])).toEqual([]);
    expect(fs.existsSync(logPath)).toBe(false);
    expect(l.peekNextSeq()).toBe(1);
  });

  it("同じインスタンスから続けて呼んでも seq が連続する", () => {
    const l = log();
    l.append([entry("あ。")]);
    l.append([entry("い。"), entry("う。")]);
    expect(readLines().map((r) => r.seq)).toEqual([1, 2, 3]);
  });

  it("プロセスを跨いでも seq が連続する（CLI は毎 delta 起動して終了するため）", () => {
    log().append([entry("あ。")]);
    log().append([entry("い。")]);
    log().append([entry("う。")]);
    expect(readLines().map((r) => r.seq)).toEqual([1, 2, 3]);
  });
});

describe("seq の state 整合", () => {
  it("state が消えてもログ末尾から復旧する（重複を出さない）", () => {
    log().append([entry("あ。"), entry("い。")]);
    fs.rmSync(statePath);

    log().append([entry("う。")]);
    expect(readLines().map((r) => r.seq)).toEqual([1, 2, 3]);
  });

  it("state が壊れていてもログ末尾から復旧する", () => {
    log().append([entry("あ。")]);
    fs.writeFileSync(statePath, "{ nextSeq: ");

    log().append([entry("い。")]);
    expect(readLines().map((r) => r.seq)).toEqual([1, 2]);
  });

  it("state が先に進んでいればそちらを尊重する（欠番は許すが重複は許さない）", () => {
    log().append([entry("あ。")]);
    fs.writeFileSync(statePath, JSON.stringify({ nextSeq: 100 }));

    log().append([entry("い。")]);
    expect(readLines().map((r) => r.seq)).toEqual([1, 100]);
  });

  it("★ state の nextSeq が安全な整数の範囲を超えていたら採番に使わない", () => {
    // Number.isInteger(1e300) は true になるので、isSafeInteger で弾かないと
    // 壊れた（あるいは手で編集された）state の値がそのまま採番に使われる
    log().append([entry("あ。")]);
    fs.writeFileSync(statePath, JSON.stringify({ nextSeq: 1e300 }));

    const records = log().append([entry("い。")]);
    // 1e300 を採用せず、ログ末尾から復旧した 2 になる
    expect(records[0]?.seq).toBe(2);
  });

  it("ログ末尾が途中で切れていても、その手前の行から拾う", () => {
    log().append([entry("あ。"), entry("い。")]);
    fs.appendFileSync(logPath, '{"seq":3,"ts":"2026'); // 追記が途中で切れた状態
    fs.rmSync(statePath);

    log().append([entry("う。")]);

    const lines = fs.readFileSync(logPath, "utf-8").split("\n").filter(Boolean);
    // 壊れた断片は断片のまま隔離され、新しいレコードは独立した行として読める
    expect(lines).toHaveLength(4);
    expect(lines[2]).toBe('{"seq":3,"ts":"2026');
    expect((JSON.parse(lines[3]!) as SpeechRecord).seq).toBe(3);
  });
});

describe("epoch — 採番のやり直しと一対一", () => {
  it("state もログも残っていれば epoch は変わらない", () => {
    const first = log();
    first.append([entry("あ。")]);

    const second = log();
    expect(second.epoch).toBe(first.epoch);
    expect(second.epochIsNew).toBe(false);
  });

  it("1回の append の中では全レコードが同じ epoch を持つ", () => {
    const l = log();
    const records = l.append([entry("あ。"), entry("い。"), entry("う。")]);
    expect(records.map((r) => r.epoch)).toEqual([l.epoch, l.epoch, l.epoch]);
  });

  it("state を消してもログ末尾から epoch を拾う（採番が続くなら epoch も続く）", () => {
    const first = log();
    first.append([entry("あ。")]);
    fs.rmSync(statePath);

    const second = log();
    expect(second.epoch).toBe(first.epoch);
    expect(second.epochIsNew).toBe(false);
  });

  it("★ state もログも消えたら epoch が変わり、epochIsNew が立つ", () => {
    const first = log();
    first.append([entry("あ。")]);
    fs.rmSync(statePath);
    fs.rmSync(logPath);

    const second = log();
    expect(second.epoch).not.toBe(first.epoch);
    expect(second.epochIsNew).toBe(true);
    // 採番もやり直されている（epoch 変化と一対一）
    expect(second.append([entry("い。")])[0]?.seq).toBe(1);
  });

  it("何も無いところから始めたら epochIsNew が立つ", () => {
    expect(log().epochIsNew).toBe(true);
  });

  it("★ 採番が復旧できて epoch だけ無いなら legacy を採る（アップグレードで世代を変えない）", () => {
    // この機能より前に書かれた state とログ（epoch フィールドが無い）
    fs.writeFileSync(logPath, `${JSON.stringify({ seq: 4, ts: "2026-08-15T00:00:00.000Z", text: "あ。" })}\n`);
    fs.writeFileSync(statePath, JSON.stringify({ nextSeq: 5 }));

    const l = log();
    expect(l.epoch).toBe("legacy");
    // ★ ここが true になると、アップグレードした瞬間に in-flight のキューが捨てられる
    expect(l.epochIsNew).toBe(false);
    expect(l.append([entry("い。")])[0]?.seq).toBe(5);
  });

  it("state に epoch があればそちらを使う", () => {
    fs.writeFileSync(logPath, `${JSON.stringify({ epoch: "from-log", seq: 1, ts: "t" })}\n`);
    fs.writeFileSync(statePath, JSON.stringify({ nextSeq: 2, epoch: "from-state" }));
    expect(log().epoch).toBe("from-state");
  });

  it("charset から外れた epoch は読まなかったことにする（パスと URL に載るため）", () => {
    fs.writeFileSync(statePath, JSON.stringify({ nextSeq: 2, epoch: "../../etc/passwd" }));
    fs.writeFileSync(logPath, `${JSON.stringify({ epoch: "ok-1", seq: 1, ts: "t" })}\n`);
    expect(log().epoch).toBe("ok-1");
  });

  it("★ seq と epoch は同じ行から採る（世代を跨いでペアにしない）", () => {
    // 新しい CLI が書いた行の後ろに、epoch を持たない行が足された状態
    fs.writeFileSync(
      logPath,
      `${JSON.stringify({ epoch: "gen-a", seq: 1, ts: "t" })}\n${JSON.stringify({ seq: 2, ts: "t" })}\n`,
    );

    // 末尾行に epoch が無いので gen-a は採らない（seq だけ採って epoch は legacy に落とす）
    const l = log();
    expect(l.epoch).toBe("legacy");
    expect(l.peekNextSeq()).toBe(3);
  });

  it("ローテート直後（現世代が空）でも、退避したファイルの末尾から epoch を拾う", () => {
    const first = log({ maxBytes: 400 });
    first.append([entry("あ".repeat(100))]);
    first.append([entry("い。")]); // ここでローテートが起きる

    // 現世代を空にして「ローテート直後」を作る。state も消して、退避側だけが情報源の状態にする
    fs.writeFileSync(logPath, "");
    fs.rmSync(statePath);
    expect(fs.existsSync(path.join(dir, "speech.1.jsonl"))).toBe(true);

    const second = log();
    expect(second.epoch).toBe(first.epoch);
    expect(second.epochIsNew).toBe(false);
  });
});

describe("ローテート", () => {
  it("上限を超えたら speech.1.jsonl に退避して新しいファイルを開く", () => {
    const l = log({ maxBytes: 400 });
    l.append([entry("あ".repeat(100))]);
    l.append([entry("い".repeat(100))]);

    expect(fs.existsSync(path.join(dir, "speech.1.jsonl"))).toBe(true);
    expect(readLines().map((r) => r.text[0])).toEqual(["い"]);
    expect(readLines(path.join(dir, "speech.1.jsonl")).map((r) => r.text[0])).toEqual(["あ"]);
  });

  it("ローテートを跨いでも seq が連続する", () => {
    const l = log({ maxBytes: 400 });
    l.append([entry("あ".repeat(100))]);
    l.append([entry("い".repeat(100))]);
    l.append([entry("う".repeat(100))]);

    expect(readLines()[0]?.seq).toBe(3);
    expect(readLines(path.join(dir, "speech.1.jsonl"))[0]?.seq).toBe(2);
  });

  it("ローテート直後に state を失っても、退避先から seq を復旧する", () => {
    const l = log({ maxBytes: 400 });
    l.append([entry("あ".repeat(100))]);
    l.append([entry("い".repeat(100))]);
    // ここで現世代は1行だけ。state を消して現世代も空にする
    fs.rmSync(statePath);
    fs.writeFileSync(logPath, "");

    log({ maxBytes: 400 }).append([entry("う。")]);
    expect(readLines().at(-1)?.seq).toBe(2);
  });

  it("★ 退避は1世代だけ（誰も tail しないので古い世代を持つ意味がない）", () => {
    const l = log({ maxBytes: 400 });
    for (const c of ["あ", "い", "う", "え"]) l.append([entry(c.repeat(100))]);

    expect(fs.existsSync(path.join(dir, "speech.1.jsonl"))).toBe(true);
    expect(fs.existsSync(path.join(dir, "speech.2.jsonl"))).toBe(false);
    // 前の退避は上書きされている
    expect(readLines(path.join(dir, "speech.1.jsonl")).map((r) => r.text[0])).toEqual(["う"]);
  });

  it("空のファイルはローテートしない（空の世代を増やさない）", () => {
    log({ maxBytes: 10 }).append([entry("あ".repeat(100))]);
    expect(fs.existsSync(path.join(dir, "speech.1.jsonl"))).toBe(false);
    expect(readLines()).toHaveLength(1);
  });
});
