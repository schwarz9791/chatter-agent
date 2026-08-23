import { describe, it, expect, vi, afterEach } from "vitest";
import type { SpeechEntry, SpeechLog } from "../core/speechLog";
import type { SpeechQueue } from "../core/speechQueue";
import type { SpeechRecord } from "../core/types";
import { createPublisher } from "./publish";

afterEach(() => {
  vi.restoreAllMocks();
});

function entry(text = "文。"): SpeechEntry {
  return {
    source: "claude-code",
    sessionId: "sess-1",
    turnId: "turn-1",
    messageId: "m1",
    kind: "assistant",
    text,
    emotion: "neutral",
  };
}

const EPOCH = "test-epoch";

function record(seq: number, text = "文。"): SpeechRecord {
  return { epoch: EPOCH, seq, ts: "2026-08-15T00:00:00.000Z", ...entry(text) };
}

/** append は渡された entries と同じ数の連番レコードを返す最小限の偽物 */
function fakeSpeechLog(epochIsNew = false): SpeechLog {
  let nextSeq = 1;
  return {
    append: vi.fn((entries: SpeechEntry[]) => entries.map((e) => record(nextSeq++, e.text))),
    peekNextSeq: () => nextSeq,
    epoch: EPOCH,
    epochIsNew,
  };
}

/** enqueue/trim だけテストごとに差し替える。他のメンバーは publish から呼ばれない */
function fakeSpeechQueue(overrides: Partial<SpeechQueue> = {}): SpeechQueue {
  return {
    enqueue: vi.fn((records: SpeechRecord[]) => records.length),
    list: () => [],
    read: () => null,
    ackUpTo: () => 0,
    dropOlderThan: () => 0,
    trim: vi.fn(() => 0),
    clear: vi.fn(() => 0),
    sweepTmp: () => 0,
    ...overrides,
  };
}

describe("createPublisher", () => {
  it("記録・配信ともに正常なら、書けたレコードをそのまま返す", () => {
    const speechLog = fakeSpeechLog();
    const speechQueue = fakeSpeechQueue();
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    const result = publish([entry("あ。"), entry("い。")]);

    expect(result).toEqual([record(1, "あ。"), record(2, "い。")]);
    expect(speechQueue.enqueue).toHaveBeenCalledWith(result);
  });

  it("enqueue が throw しても publish は throw せず、append の戻り値をそのまま返す", () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const speechLog = fakeSpeechLog();
    const speechQueue = fakeSpeechQueue({
      enqueue: vi.fn(() => {
        throw new Error("ENOSPC");
      }),
    });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    // ★ 修正前の実装（speechQueue.enqueue(records) を直接呼ぶだけ）はここで例外を
    //   投げて抜けていた。呼び出し側（worker.ts の processMessage）は removeEntry に
    //   到達できず、spool の entry を消せないまま次の CLI が同じメッセージを組み直していた
    //   （#30 以降、組み直される単位は1文ではなくメッセージ全文）
    let result: SpeechRecord[] | undefined;
    expect(() => {
      result = publish([entry()]);
    }).not.toThrow();

    expect(result).toEqual([record(1)]);
    expect(consoleError).toHaveBeenCalled();
  });

  it("trim が throw しても publish は throw しない", () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const speechLog = fakeSpeechLog();
    const speechQueue = fakeSpeechQueue({
      trim: vi.fn(() => {
        throw new Error("EACCES");
      }),
    });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    let result: SpeechRecord[] | undefined;
    expect(() => {
      result = publish([entry()]);
    }).not.toThrow();

    expect(result).toEqual([record(1)]);
    expect(consoleError).toHaveBeenCalled();
  });

  it("trim が捨てた件数と、enqueue が書けなかった件数をログに出す", () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const speechLog = fakeSpeechLog();
    const speechQueue = fakeSpeechQueue({
      // 2件渡すのに1件しか書けなかった、という不一致
      enqueue: vi.fn(() => 1),
      trim: vi.fn(() => 3),
    });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    publish([entry("あ。"), entry("い。")]);

    const messages = consoleError.mock.calls.map((call) => call.join(" "));
    expect(messages.some((m) => m.includes("2") && m.includes("1"))).toBe(true); // enqueue の不一致
    expect(messages.some((m) => m.includes("3"))).toBe(true); // trim が捨てた件数
  });

  it("maxEntries は publish のたびに読み直す（起動時の1回きりで固定しない）", () => {
    const speechLog = fakeSpeechLog();
    const speechQueue = fakeSpeechQueue();
    const maxEntries = vi.fn(() => 500);
    const publish = createPublisher({ speechLog, speechQueue, maxEntries });

    publish([entry()]);
    publish([entry()]);

    expect(maxEntries).toHaveBeenCalledTimes(2);
  });
});

describe("採番のやり直し（epochIsNew）", () => {
  it("epoch が新規なら、最初の publish で旧世代のキューを空にする", () => {
    const speechLog = fakeSpeechLog(true);
    const speechQueue = fakeSpeechQueue({ clear: vi.fn(() => 7) });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    publish([entry()]);

    expect(speechQueue.clear).toHaveBeenCalledTimes(1);
  });

  it("★ clear は append より前に呼ぶ（間で kill されると掃除が永久に走らなくなる）", () => {
    // append は新しい epoch を speech.state.json に永続化する。その後ろで clear すると、
    // 隙間で kill されたときに次のプロセスが epochIsNew === false になり、
    // **以後どのプロセスも掃除しない**。CLI は hook から毎 delta デタッチ起動される
    const calls: string[] = [];
    const speechLog = fakeSpeechLog(true);
    vi.mocked(speechLog.append).mockImplementation((entries: SpeechEntry[]) => {
      calls.push("append");
      return entries.map((e, i) => record(i + 1, e.text));
    });
    const speechQueue = fakeSpeechQueue({
      clear: vi.fn(() => {
        calls.push("clear");
        return 3;
      }),
      enqueue: vi.fn((records: SpeechRecord[]) => {
        calls.push("enqueue");
        return records.length;
      }),
    });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    publish([entry()]);

    expect(calls).toEqual(["clear", "append", "enqueue"]);
  });

  it("2回目以降の publish では clear しない", () => {
    const speechLog = fakeSpeechLog(true);
    const speechQueue = fakeSpeechQueue({ clear: vi.fn(() => 0) });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    publish([entry()]);
    publish([entry()]);
    publish([entry()]);

    expect(speechQueue.clear).toHaveBeenCalledTimes(1);
  });

  it("epoch が続いているなら一度も clear しない", () => {
    const speechLog = fakeSpeechLog(false);
    const speechQueue = fakeSpeechQueue({ clear: vi.fn(() => 0) });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    publish([entry()]);

    expect(speechQueue.clear).not.toHaveBeenCalled();
  });

  it("clear が throw しても発話は止めない", () => {
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => {});
    const speechLog = fakeSpeechLog(true);
    const speechQueue = fakeSpeechQueue({
      clear: vi.fn(() => {
        throw new Error("EACCES");
      }),
    });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    const result = publish([entry("あ。")]);

    expect(result).toEqual([record(1, "あ。")]);
    expect(speechQueue.enqueue).toHaveBeenCalledWith(result);
    expect(consoleError).toHaveBeenCalled();
  });

  it("★ clear が throw したら、次の publish で再試行する", () => {
    // フラグを try の外で立てると、そのプロセスでも次のプロセスでも
    // （append が state を書いてしまうので epochIsNew が false になる）再試行されない。
    // 旧世代が残ったままだと trim が seq 昇順で「今書いた新しい entry から」捨て始める
    vi.spyOn(console, "error").mockImplementation(() => {});
    const speechLog = fakeSpeechLog(true);
    let attempts = 0;
    const speechQueue = fakeSpeechQueue({
      clear: vi.fn(() => {
        attempts++;
        if (attempts === 1) throw new Error("EACCES");
        return 3;
      }),
    });
    const publish = createPublisher({ speechLog, speechQueue, maxEntries: () => 500 });

    publish([entry()]);
    publish([entry()]);
    publish([entry()]);

    // 1回目が失敗 → 2回目で成功 → 3回目はもう呼ばない
    expect(attempts).toBe(2);
  });
});
