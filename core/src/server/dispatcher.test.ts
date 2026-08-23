import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createSpeechQueue, type SpeechQueue } from "../core/speechQueue";
import type { SpeechRecord } from "../core/types";
import { createDispatcher } from "./dispatcher";

let dir: string;
let queue: SpeechQueue;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-dispatcher-"));
  queue = createSpeechQueue(path.join(dir, "speech"));
});

afterEach(() => {
  // ★ アサーション失敗で早期リターンしても console.warn の spy が次のテストへ
  //   漏れないよう、テスト本体の mockRestore() とは別にここでも必ず戻す
  vi.restoreAllMocks();
  fs.rmSync(dir, { recursive: true, force: true });
});

function record(seq: number, text = `文${seq}。`): SpeechRecord {
  return {
    epoch: "test-epoch",
    seq,
    ts: "2026-08-15T00:00:00.000Z",
    source: "claude-code",
    sessionId: "sess-1",
    turnId: "turn-1",
    messageId: "m1",
    kind: "assistant",
    text,
    emotion: "neutral",
  };
}

/** broadcast に渡された line から seq だけ取り出す */
function seqsBroadcast(broadcast: ReturnType<typeof vi.fn>): number[] {
  return broadcast.mock.calls.map((call) => (JSON.parse(call[0] as string) as { seq: number }).seq);
}

describe("poll", () => {
  it("未配信の entry を昇順に broadcast する", () => {
    queue.enqueue([record(2), record(1)]);
    const broadcast = vi.fn();
    const dispatcher = createDispatcher({ queue, broadcast });

    dispatcher.poll();

    expect(seqsBroadcast(broadcast)).toEqual([1, 2]);
  });

  it("同じ seq を二度 broadcast しない", () => {
    queue.enqueue([record(1)]);
    const broadcast = vi.fn();
    const dispatcher = createDispatcher({ queue, broadcast });

    dispatcher.poll();
    dispatcher.poll();

    expect(broadcast).toHaveBeenCalledTimes(1);
  });

  it("★ 採番のやり直し: キューが空になった後、新しく振られた低い seq も配信する", () => {
    queue.enqueue([record(100), record(101)]);
    const broadcast = vi.fn();
    const dispatcher = createDispatcher({ queue, broadcast });

    dispatcher.poll();
    expect(seqsBroadcast(broadcast)).toEqual([100, 101]);

    // 100, 101 がキューから消える（ack 等を想定）
    queue.ackUpTo(101);
    // ~/.config/chatter-agent の削除やバックアップ復元を想定し、seq が 1 から振り直される
    queue.enqueue([record(1), record(2)]);

    dispatcher.poll();

    // 「水位ひとつ」の実装（sentUpTo=101）だと 1, 2 は sentUpTo 以下なので配信されない
    expect(seqsBroadcast(broadcast)).toEqual([100, 101, 1, 2]);
  });

  it("★ 採番のやり直し（古い未 ack が残っている場合）でも新しい seq を配信する", () => {
    // 水位ひとつの実装ではここが落ちる: 最大 seq(500) が水位のままなのでリセット判定が
    // 発火せず、新しい seq 1, 2 は「水位以下」として永久に捨てられる
    const records: SpeechRecord[] = [];
    for (let seq = 400; seq <= 500; seq++) records.push(record(seq));
    queue.enqueue(records);

    const broadcast = vi.fn();
    const dispatcher = createDispatcher({ queue, broadcast });
    dispatcher.poll();
    expect(broadcast).toHaveBeenCalledTimes(101);

    // 400-500 は ack されないまま残り、新たに 1, 2 が採番し直されて積まれる
    queue.enqueue([record(1), record(2)]);
    dispatcher.poll();

    const seqs = seqsBroadcast(broadcast);
    expect(seqs).toContain(1);
    expect(seqs).toContain(2);
  });

  it("★ read() が null を返す entry を飛ばし、2回目の poll で再び警告しない", () => {
    queue.enqueue([record(1)]);
    // seq=2 に対応するファイルを、payload の seq がファイル名と食い違う壊れた内容で置く
    // （speechQueue.read() が null を返す条件。protocol.md）
    fs.writeFileSync(path.join(dir, "speech", "000000000002.json"), `${JSON.stringify(record(99))}\n`);

    const broadcast = vi.fn();
    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => {});
    const dispatcher = createDispatcher({ queue, broadcast });

    dispatcher.poll();
    expect(seqsBroadcast(broadcast)).toEqual([1]); // seq=2 は飛ばされる
    expect(warnSpy).toHaveBeenCalledTimes(1);

    dispatcher.poll();
    expect(warnSpy).toHaveBeenCalledTimes(1); // 2回目は再警告しない
    expect(broadcast).toHaveBeenCalledTimes(1); // broadcast も増えない

    warnSpy.mockRestore();
  });
});

describe("ack", () => {
  it("★ 配信済みの範囲に頭を押さえる: MAX_SAFE_INTEGER を投げても未配信は消えない", () => {
    queue.enqueue([record(1), record(2), record(3)]);
    const dispatcher = createDispatcher({ queue, broadcast: vi.fn() });
    dispatcher.poll(); // 配信済みが seq 3 まで、という状態を作る

    // 4, 5 はまだ poll していない＝未配信
    queue.enqueue([record(4), record(5)]);

    dispatcher.ack(Number.MAX_SAFE_INTEGER);

    // クランプが無いと ackUpTo(MAX_SAFE_INTEGER) がキューを全消しし、4, 5 も消える
    expect(queue.list()).toEqual([4, 5]);
  });

  it("配信済み分のみ消す", () => {
    queue.enqueue([record(1), record(2), record(3)]);
    const dispatcher = createDispatcher({ queue, broadcast: vi.fn() });
    dispatcher.poll();

    dispatcher.ack(2);

    expect(queue.list()).toEqual([3]);
  });

  it("配信済みが無ければ何もしない", () => {
    queue.enqueue([record(1)]);
    const dispatcher = createDispatcher({ queue, broadcast: vi.fn() });
    // poll していない＝まだ何も配信済みではない

    dispatcher.ack(1);

    expect(queue.list()).toEqual([1]);
  });
});

describe("catchUp", () => {
  it("★ 配信済みのものだけを送る", () => {
    queue.enqueue([record(1), record(2), record(3)]);
    const dispatcher = createDispatcher({ queue, broadcast: vi.fn() });
    dispatcher.poll(); // 1, 2, 3 を配信済みにする

    queue.enqueue([record(4)]); // まだ配信していない

    const sent: number[] = [];
    dispatcher.catchUp((line) => {
      sent.push((JSON.parse(line) as { seq: number }).seq);
      return true;
    });

    expect(sent).toEqual([1, 2, 3]);
  });

  it("★ send が false を返したら打ち切り、その旨をログに出す", () => {
    queue.enqueue([record(1), record(2), record(3)]);
    const dispatcher = createDispatcher({ queue, broadcast: vi.fn() });
    dispatcher.poll();

    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => {});
    const sent: number[] = [];
    dispatcher.catchUp((line) => {
      sent.push((JSON.parse(line) as { seq: number }).seq);
      return sent.length < 2; // 2件目で打ち切る
    });

    expect(sent).toEqual([1, 2]);
    expect(warnSpy).toHaveBeenCalledTimes(1);
    expect(String(warnSpy.mock.calls[0]?.[0])).toMatch(/打ち切/);

    warnSpy.mockRestore();
  });

  it("完走したときは打ち切りの警告を出さない", () => {
    queue.enqueue([record(1), record(2)]);
    const dispatcher = createDispatcher({ queue, broadcast: vi.fn() });
    dispatcher.poll();

    const warnSpy = vi.spyOn(console, "warn").mockImplementation(() => {});
    dispatcher.catchUp(() => true);

    expect(warnSpy).not.toHaveBeenCalled();
    warnSpy.mockRestore();
  });
});
