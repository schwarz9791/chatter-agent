import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createSpeechQueue } from "./speechQueue";
import type { SpeechRecord } from "./types";

// ★ fs は Node の組み込みモジュールで、ESM では名前空間が configurable でないため
//   `vi.spyOn(fs, "writeFileSync")` は "Cannot redefine property" で落ちる。
//   `vi.mock` でモジュールごと差し替え、書き込みの経路を検証したい2関数だけ vi.fn でラップする
//   （core/lock.test.ts と同じ手法）。既定の実装は本物（actual）へ委譲する
const actualFsRef = vi.hoisted(() => ({ current: null as typeof import("fs") | null }));

vi.mock("fs", async (importOriginal) => {
  const actual = await importOriginal<typeof import("fs")>();
  actualFsRef.current = actual;
  return {
    ...actual,
    writeFileSync: vi.fn(actual.writeFileSync),
    renameSync: vi.fn(actual.renameSync),
  };
});

let dir: string;
let queueDir: string;

beforeEach(() => {
  // ★ vi.fn の呼び出し履歴はテストを跨いで蓄積する（vi.mock はファイル読み込み時に1回だけ
  //   走る）。実装（actual への委譲）はそのままに呼び出し回数だけ毎回リセットする
  vi.clearAllMocks();
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-queue-"));
  queueDir = path.join(dir, "speech");
});

afterEach(() => {
  // ★ テストごとに mockImplementationOnce / mockImplementation で差し替えた分を、
  //   毎回「本物へ委譲する」既定の状態へ戻す（lock.test.ts と同じ理由）
  const actual = actualFsRef.current;
  if (actual) {
    vi.mocked(fs.writeFileSync).mockImplementation(actual.writeFileSync);
    vi.mocked(fs.renameSync).mockImplementation(actual.renameSync);
  }
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

const queue = () => createSpeechQueue(queueDir);
const fileNames = () => fs.readdirSync(queueDir).sort();
const fileNameFor = (seq: number) => `${String(seq).padStart(12, "0")}.json`;

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

  it("空配列では何もしない", () => {
    queue().enqueue([]);
    expect(fileNames()).toEqual([]);
  });

  it("書けた件数を返す", () => {
    expect(queue().enqueue([record(1), record(2), record(3)])).toBe(3);
  });

  it("空配列では0を返す", () => {
    expect(queue().enqueue([])).toBe(0);
  });

  it("★ tmp + rename で書く（renameSync が enqueue 件数ぶん、常に .tmp からの改名になる）", () => {
    // 素の writeFileSync に置き換えても「一時ファイルが残っていない」は自明に真になってしまうので、
    // 実際の書き込み経路（renameSync の呼び出し）そのものを見る
    queue().enqueue([record(1), record(2), record(3)]);

    expect(fs.renameSync).toHaveBeenCalledTimes(3);
    for (const [from, to] of vi.mocked(fs.renameSync).mock.calls) {
      expect(String(from)).toMatch(/\.tmp$/);
      expect(String(to)).not.toMatch(/\.tmp$/);
    }
    // 改名先の実体だけが残り、tmp は残らない
    expect(fileNames().filter((f) => f.endsWith(".tmp"))).toEqual([]);
  });

  it("★ rename されるまでは読めない（tmp を経由して書くことを見る）", () => {
    // writeFileSync（tmp への書き込み）が終わった直後・renameSync の前というタイミングで
    // read() を呼び、まだ最終ファイルとしては見えないことを確認する。素の writeFileSync で
    // 直接最終ファイルへ書く実装だと、この時点で既に読めてしまい fail する
    let seenDuringWrite: SpeechRecord | null | undefined;
    const q = queue();

    vi.mocked(fs.writeFileSync).mockImplementation((...args: Parameters<typeof fs.writeFileSync>) => {
      const result = actualFsRef.current!.writeFileSync(...args);
      if (String(args[0]).includes("000000000002")) {
        seenDuringWrite = q.read(2);
      }
      return result;
    });

    q.enqueue([record(1), record(2), record(3)]);

    expect(seenDuringWrite).toBeNull();
    expect(q.read(2)).not.toBeNull(); // rename 後は普通に読める
  });

  it("1件が失敗しても残りは書く。書けた件数だけを返す", () => {
    const q = queue();
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => {});

    vi.mocked(fs.writeFileSync).mockImplementation((...args: Parameters<typeof fs.writeFileSync>) => {
      if (String(args[0]).includes("000000000002")) throw new Error("disk full");
      return actualFsRef.current!.writeFileSync(...args);
    });

    const written = q.enqueue([record(1), record(2), record(3)]);

    expect(written).toBe(2);
    expect(q.list()).toEqual([1, 3]);
    expect(errorSpy).toHaveBeenCalled();
    errorSpy.mockRestore();
  });
});

describe("list", () => {
  it("seq 昇順で返す（ファイル名の辞書順ではなく数値順）", () => {
    const q = queue();
    q.enqueue([record(2), record(10), record(1)]);
    expect(q.list()).toEqual([1, 2, 10]);
  });

  it("空なら空配列", () => {
    expect(queue().list()).toEqual([]);
  });

  it("★ .tmp を無視する", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(queueDir, "000000000002.json.tmp"), '{"seq":2');

    expect(q.list()).toEqual([1]);
  });

  it("seq として読めない名前は無視する", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(queueDir, "README.txt"), "x");
    fs.writeFileSync(path.join(queueDir, "abc.json"), "{}");
    fs.writeFileSync(path.join(queueDir, "-1.json"), "{}");

    expect(q.list()).toEqual([1]);
  });

  it("中身は読まない（壊れていても一覧には出る）", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(queueDir, "000000000002.json"), "not json");

    expect(q.list()).toEqual([1, 2]);
  });

  it("ディレクトリが消えていても落ちない", () => {
    const q = queue();
    fs.rmSync(queueDir, { recursive: true, force: true });
    expect(q.list()).toEqual([]);
  });
});

describe("read", () => {
  it("パース済みのレコードを返す", () => {
    const q = queue();
    q.enqueue([record(1, "あ。")]);
    // ★ 配信側は epoch を見て世代を判定し、正規化した内容を組み直して流すので、中身が要る
    expect(q.read(1)).toMatchObject({ epoch: "test-epoch", seq: 1, text: "あ。" });
  });

  it("無ければ null", () => {
    expect(queue().read(1)).toBeNull();
  });

  it("空ファイルなら null", () => {
    const q = queue();
    fs.writeFileSync(path.join(queueDir, "000000000001.json"), "");
    expect(q.read(1)).toBeNull();
  });

  it("★ JSON でない中身なら null", () => {
    const q = queue();
    fs.writeFileSync(path.join(queueDir, "000000000001.json"), "not json");
    expect(q.read(1)).toBeNull();
  });

  it("★ payload の seq がファイル名と食い違うなら null（順序・ack・重複排除がずれる事故を防ぐ）", () => {
    const q = queue();
    // ファイル名は seq=1 だが、中身は seq=2 のレコード（壊れた・手で置かれたファイルを想定）
    fs.writeFileSync(path.join(queueDir, "000000000001.json"), `${JSON.stringify(record(2))}\n`);
    expect(q.read(1)).toBeNull();
  });

  it("★ [B-1] epoch が無い entry は legacy として読む（epoch 導入前の in-flight）", () => {
    // 未 ack の entry を残したままアップグレードすると起こる。ここで弾くと、
    // ワイヤ上で epoch 無しのフレームになり**クライアントが全部捨てて何も喋らない**
    const q = queue();
    const { epoch: _dropped, ...withoutEpoch } = record(1, "あ。");
    fs.writeFileSync(path.join(queueDir, "000000000001.json"), `${JSON.stringify(withoutEpoch)}\n`);

    expect(q.read(1)).toMatchObject({ epoch: "legacy", seq: 1, text: "あ。" });
  });

  it("★ [C-3] epoch が載っているのに形が違うなら null（世代の判定に食わせる値なので）", () => {
    const q = queue();
    for (const [i, epoch] of [{}, 123, "", "../../etc/passwd", "a".repeat(65)].entries()) {
      const seq = i + 1;
      const name = `${String(seq).padStart(12, "0")}.json`;
      fs.writeFileSync(path.join(queueDir, name), `${JSON.stringify({ ...record(seq), epoch })}\n`);
      expect(q.read(seq), JSON.stringify(epoch)).toBeNull();
    }
  });

  it("★ [C-3] ts が日付として読めないなら null（字句比較で世代が永久に勝つのを防ぐ）", () => {
    // `ts: "z"` ひとつで `"z" > "2026-…"` になり、その世代が永久に勝つ
    const q = queue();
    for (const [i, ts] of ["z", "", 7, null, "not a date"].entries()) {
      const seq = i + 1;
      const name = `${String(seq).padStart(12, "0")}.json`;
      fs.writeFileSync(path.join(queueDir, name), `${JSON.stringify({ ...record(seq), ts })}\n`);
      expect(q.read(seq), JSON.stringify(ts)).toBeNull();
    }
  });

  it("壊れたファイルを消したりはしない（判断は呼び出し側に残す）", () => {
    const q = queue();
    const target = path.join(queueDir, "000000000001.json");
    fs.writeFileSync(target, "not json");

    q.read(1);

    expect(fs.existsSync(target)).toBe(true);
  });
});

describe("ackUpTo", () => {
  it("seq <= upTo を消す", () => {
    const q = queue();
    q.enqueue([record(1), record(2), record(3)]);

    expect(q.ackUpTo(2)).toBe(2);
    expect(q.list()).toEqual([3]);
  });

  it("累積 ack なので、1つ飛ばしても取り残さない", () => {
    const q = queue();
    q.enqueue([record(1), record(2), record(3), record(4)]);

    // seq=2 の ack が届かず、次に seq=4 まで喋ったと言われた場合
    expect(q.ackUpTo(4)).toBe(4);
    expect(q.list()).toEqual([]);
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
    expect(q.list()).toEqual([1]);
  });
});

describe("dropOlderThan", () => {
  it("古いものだけ消し、新しいものを残す", () => {
    const q = queue();
    const now = 1_000_000;

    q.enqueue([record(1), record(2)]);
    const old = new Date(now - 20_000);
    fs.utimesSync(path.join(queueDir, fileNameFor(1)), old, old);
    fs.utimesSync(path.join(queueDir, fileNameFor(2)), old, old);

    q.enqueue([record(3)]);
    const fresh = new Date(now - 1_000);
    fs.utimesSync(path.join(queueDir, fileNameFor(3)), fresh, fresh);

    expect(q.dropOlderThan(10_000, now)).toBe(2);
    expect(q.list()).toEqual([3]);
  });

  it("しきい値ちょうどなら残す", () => {
    const q = queue();
    const now = 1_000_000;

    q.enqueue([record(1)]);
    const boundary = new Date(now - 10_000);
    fs.utimesSync(path.join(queueDir, fileNameFor(1)), boundary, boundary);

    expect(q.dropOlderThan(10_000, now)).toBe(0);
    expect(q.list()).toEqual([1]);
  });

  it("空でも落ちない", () => {
    expect(queue().dropOlderThan(10_000)).toBe(0);
  });

  it("キュー以外のファイルは消さない", () => {
    const q = queue();
    const now = 1_000_000;
    q.enqueue([record(1)]);
    const old = new Date(now - 20_000);
    fs.utimesSync(path.join(queueDir, fileNameFor(1)), old, old);
    fs.writeFileSync(path.join(queueDir, "README.txt"), "x");

    q.dropOlderThan(10_000, now);
    expect(fileNames()).toEqual(["README.txt"]);
  });
});

describe("trim", () => {
  it("上限を超えたぶんを古い方から捨てる", () => {
    const q = queue();
    q.enqueue([record(1), record(2), record(3), record(4), record(5)]);

    expect(q.trim(2)).toBe(3);
    expect(q.list()).toEqual([4, 5]);
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

describe("clear", () => {
  it("全部消して件数を返す", () => {
    const q = queue();
    q.enqueue([record(1), record(2), record(3)]);

    expect(q.clear()).toBe(3);
    expect(q.list()).toEqual([]);
  });

  it("空のキューでは 0", () => {
    expect(queue().clear()).toBe(0);
  });

  it("★ 採番のやり直しで trim が壊れる形を、clear が解く", () => {
    const q = queue();
    // 旧世代が未 ack で残っているところに、採番がやり直されて 1 から書き直された状態
    q.enqueue([record(400), record(401)]);
    q.enqueue([record(1), record(2)]);

    // trim は seq 昇順で捨てるので、この状態では**今書いた新しい方から**消える
    const stale = queue();
    stale.trim(2);
    expect(stale.list()).toEqual([400, 401]);

    // clear してから書けば、その倒錯が起きない
    const q2 = queue();
    q2.clear();
    q2.enqueue([record(1), record(2)]);
    expect(q2.trim(2)).toBe(0);
    expect(q2.list()).toEqual([1, 2]);
  });

  it("キュー以外のファイルは消さない", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(dir, "notes.txt"), "x");

    q.clear();
    expect(fs.existsSync(path.join(dir, "notes.txt"))).toBe(true);
  });
});

describe("sweepTmp", () => {
  it(".tmp だけ消す", () => {
    const q = queue();
    q.enqueue([record(1)]);
    fs.writeFileSync(path.join(queueDir, "000000000002.json.tmp"), "x");
    fs.writeFileSync(path.join(queueDir, "README.txt"), "x");

    expect(q.sweepTmp()).toBe(1);
    expect(fileNames()).toEqual(["000000000001.json", "README.txt"]);
  });

  it("何もなければ0件", () => {
    expect(queue().sweepTmp()).toBe(0);
  });

  it("ディレクトリが消えていても落ちない", () => {
    const q = queue();
    fs.rmSync(queueDir, { recursive: true, force: true });
    expect(q.sweepTmp()).toBe(0);
  });
});
