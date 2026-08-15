import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createSpeechQueue } from "./speechQueue";
import type { SpeechRecord } from "./types";

let dir: string;
let queueDir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-queue-"));
  queueDir = path.join(dir, "speech");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

function record(seq: number, text = `文${seq}。`): SpeechRecord {
  return {
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

const queue = () => createSpeechQueue(queueDir);
const fileNames = () => fs.readdirSync(queueDir).sort();

describe("enqueue", () => {
  it("ディレクトリが無ければ作る", () => {
    queue();
    expect(fs.existsSync(queueDir)).toBe(true);
  });

  it("1文1ファイルで書き、ファイル名が seq になる", () => {
    queue().enqueue([record(1), record(2)]);
    expect(fileNames()).toEqual(["000000000001.json", "000000000002.json"]);
  });

  it("中身は speech.jsonl の1行と同じ", () => {
    queue().enqueue([record(7, "確認します。")]);
    const line = fs.readFileSync(path.join(queueDir, "000000000007.json"), "utf-8").trim();
    expect(JSON.parse(line)).toEqual(record(7, "確認します。"));
  });

  it("★ 一時ファイルを残さない（server が書きかけを読まないように）", () => {
    queue().enqueue([record(1), record(2), record(3)]);
    expect(fileNames().filter((f) => f.endsWith(".tmp"))).toEqual([]);
  });

  it("空配列では何もしない", () => {
    queue().enqueue([]);
    expect(fileNames()).toEqual([]);
  });
});

describe("readAll", () => {
  it("seq 昇順で返す（ファイル名の辞書順ではなく数値順）", () => {
    const q = queue();
    q.enqueue([record(2), record(10), record(1)]);
    expect(q.readAll().map((e) => e.seq)).toEqual([1, 2, 10]);
  });

  it("そのまま配信できる1行を返す", () => {
    const q = queue();
    q.enqueue([record(1, "あ。")]);
    expect(JSON.parse(q.readAll()[0]!.line)).toMatchObject({ seq: 1, text: "あ。" });
  });

  it("空なら空配列", () => {
    expect(queue().readAll()).toEqual([]);
  });

  it("★ 書きかけの一時ファイルは読まない", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(queueDir, "000000000002.json.tmp"), '{"seq":2');

    expect(q.readAll().map((e) => e.seq)).toEqual([1]);
  });

  it("seq として読めない名前は無視する", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(queueDir, "README.txt"), "x");
    fs.writeFileSync(path.join(queueDir, "abc.json"), "{}");
    fs.writeFileSync(path.join(queueDir, "-1.json"), "{}");

    expect(q.readAll().map((e) => e.seq)).toEqual([1]);
  });

  it("空ファイルは飛ばす", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(queueDir, "000000000002.json"), "");

    expect(q.readAll().map((e) => e.seq)).toEqual([1]);
  });

  it("ディレクトリが消えていても落ちない", () => {
    const q = queue();
    fs.rmSync(queueDir, { recursive: true, force: true });
    expect(q.readAll()).toEqual([]);
  });
});

describe("ackUpTo", () => {
  it("seq <= upTo を消す", () => {
    const q = queue();
    q.enqueue([record(1), record(2), record(3)]);

    expect(q.ackUpTo(2)).toBe(2);
    expect(q.readAll().map((e) => e.seq)).toEqual([3]);
  });

  it("累積 ack なので、1つ飛ばしても取り残さない", () => {
    const q = queue();
    q.enqueue([record(1), record(2), record(3), record(4)]);

    // seq=2 の ack が届かず、次に seq=4 まで喋ったと言われた場合
    expect(q.ackUpTo(4)).toBe(4);
    expect(q.readAll()).toEqual([]);
  });

  it("該当が無ければ0件", () => {
    const q = queue();
    q.enqueue([record(5)]);
    expect(q.ackUpTo(4)).toBe(0);
  });

  it("★ クライアント由来の値でパスを組み立てない", () => {
    const q = queue();
    q.enqueue([record(1)]);

    // 不正な値は無視する。ファイル名から読んだ seq と比較するだけなので、
    // どんな文字列が来てもパスにはならない
    for (const bogus of [-1, 1.5, Number.NaN, Number.POSITIVE_INFINITY, 2 ** 60]) {
      expect(q.ackUpTo(bogus)).toBe(0);
    }
    expect(q.readAll().map((e) => e.seq)).toEqual([1]);
  });
});

describe("clear", () => {
  it("全部消す", () => {
    const q = queue();
    q.enqueue([record(1), record(2)]);

    expect(q.clear()).toBe(2);
    expect(q.readAll()).toEqual([]);
  });

  it("空でも落ちない", () => {
    expect(queue().clear()).toBe(0);
  });

  it("キュー以外のファイルは消さない", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(queueDir, "README.txt"), "x");

    q.clear();
    expect(fileNames()).toEqual(["README.txt"]);
  });
});

describe("trim", () => {
  it("上限を超えたぶんを古い方から捨てる", () => {
    const q = queue();
    q.enqueue([record(1), record(2), record(3), record(4), record(5)]);

    expect(q.trim(2)).toBe(3);
    expect(q.readAll().map((e) => e.seq)).toEqual([4, 5]);
  });

  it("上限内なら何もしない", () => {
    const q = queue();
    q.enqueue([record(1), record(2)]);
    expect(q.trim(5)).toBe(0);
  });

  it("ちょうど上限なら何もしない", () => {
    const q = queue();
    q.enqueue([record(1), record(2)]);
    expect(q.trim(2)).toBe(0);
  });

  it("上限0なら全部捨てる", () => {
    const q = queue();
    q.enqueue([record(1), record(2)]);
    expect(q.trim(0)).toBe(2);
  });
});
