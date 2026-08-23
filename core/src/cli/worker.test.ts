import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { acquireLock } from "../core/lock";
import { createSpeechLog } from "../core/speechLog";
import { createSpeechQueue } from "../core/speechQueue";
import type { SpeechRecord } from "../core/types";
import { removeEntry, scanSpool } from "./spool";
import { acquireLockWithRetry, drainSpool } from "./worker";
import type { DrainDeps } from "./worker";
import { addSummarizerSession, emptyWorkerState, readWorkerState, writeWorkerState } from "./workerState";

// ★ scanSpool / removeEntry は「進展の無いパスの直後に届いた spool」の再現や、削除失敗の
//   再現に差し替えが要る。モジュールごと差し替え、この2つだけ vi.fn でラップする
//   （./spool.ts の他のエクスポートは実体のまま素通りさせる）。パターンは core/lock.test.ts と同じ
const actualSpoolRef = vi.hoisted(() => ({ current: null as typeof import("./spool") | null }));

vi.mock("./spool", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./spool")>();
  actualSpoolRef.current = actual;
  return {
    ...actual,
    scanSpool: vi.fn(actual.scanSpool),
    removeEntry: vi.fn(actual.removeEntry),
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
  if (actual) {
    vi.mocked(scanSpool).mockImplementation(actual.scanSpool);
    vi.mocked(removeEntry).mockImplementation(actual.removeEntry);
  }
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
    // 既定は素通し（要約しない）。要約の挙動そのものを見るテストだけ個別に差し替える
    summarize: (text) => text,
    now: () => clock,
    ...overrides,
  });
}

/**
 * spool に delta を1本置く（plugin の bash hook が `<message_id>.<index>.json` を
 * tmp + rename で置くのと同じ結果になればよいので、テストでは直接 writeFileSync でよい）。
 */
function appendDelta(
  messageId: string,
  index: number,
  text: string,
  final = false,
  sessionId = "sess-1",
  // ★ [#33] 実機では MessageDisplay と PreToolUse が同じ prompt_id を持つ。既定を
  //   `writePrompt` 側と揃えておかないと、引き上げのテストだけが特殊な payload になる
  promptId = "p1",
): void {
  fs.writeFileSync(
    path.join(spoolDir, `${messageId}.${index}.json`),
    JSON.stringify({
      session_id: sessionId,
      prompt_id: promptId,
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
  it("★ final:true が来るまで1文も書かない", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    expect(drain().written).toBe(0);
    expect(texts()).toEqual([]);
  });

  it("final:true が来るまで spool を消さない", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(true);
  });

  it("★ 進捗サイドカーを作らない", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();
    expect(fs.readdirSync(spoolDir).filter((f) => f.endsWith(".progress.json"))).toEqual([]);
  });

  it("final:true でメッセージ全文が一括で出て、spool が消える", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。", true);
    drain();
    expect(fs.readdirSync(spoolDir)).toEqual([]);
    expect(texts()).toEqual(["確認します。", "ログを見ます。"]);
  });

  it("delta を跨いだ文も、final でまとめて1回だけ書く", () => {
    appendDelta("m1", 0, "あ。い");
    drain();
    appendDelta("m1", 1, "。う。");
    drain();
    expect(texts()).toEqual([]);

    appendDelta("m1", 2, "え。", true);
    drain();
    expect(texts()).toEqual(["あ。", "い。", "う。", "え。"]);
  });

  it("★ index に欠番があるうちは何も出ない。埋まったら全文出る", () => {
    appendDelta("m1", 0, "あ。");
    appendDelta("m1", 2, "う。", true); // index 1 がまだ届いていない
    drain();
    expect(texts()).toEqual([]);

    appendDelta("m1", 1, "い。");
    drain();
    expect(texts()).toEqual(["あ。", "い。", "う。"]);
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

/**
 * [#30] で保証が付いた契約。クライアントは `(seq, ts)` で重複排除し、`messageId` で
 * まとめてよくなる（docs/protocol.md「発話の粒度」）。
 *
 * [#30]: https://github.com/schwarz9791/chatter-agent/issues/30
 */
describe("発話の粒度（契約）", () => {
  it("★ 1メッセージ分の seq が連続し、ts が同値で、messageId が同一", () => {
    appendDelta("m1", 0, "あ。い。う。", true);
    drain();

    const rows = records();
    expect(rows).toHaveLength(3);
    expect(rows.map((r) => r.seq)).toEqual([1, 2, 3]);
    expect(new Set(rows.map((r) => r.ts)).size).toBe(1);
    expect(new Set(rows.map((r) => r.messageId))).toEqual(new Set(["m1"]));
  });

  it("★ publish はメッセージあたり1回しか呼ばれない", () => {
    appendDelta("m1", 0, "あ。い");
    drain();
    appendDelta("m1", 1, "。う。え。", true);

    const calls: number[] = [];
    drain({
      publish: (entries) => {
        calls.push(entries.length);
        return entries.map((entry, i) => ({ ...entry, seq: i + 1, ts: "2026-08-16T00:00:00.000Z" }));
      },
    });

    expect(calls).toEqual([4]);
  });

  it("別メッセージは別の ts を持つ（バッチの境界が読める）", () => {
    appendDelta("m1", 0, "あ。", true);
    drain();
    clock += 1_000;
    tick();
    appendDelta("m2", 0, "い。", true);
    drain();

    const rows = records();
    expect(rows[0]?.ts).not.toBe(rows[1]?.ts);
  });
});

/**
 * `final` が来なかったメッセージ（ESC 中断・クラッシュ・index 欠番）の取りこぼしを、
 * 後続イベントの到着で拾う経路。通常の発話は `final:true` が駆動する。
 */
describe("final が来なかったメッセージの救済", () => {
  it("別メッセージが始まったら、前メッセージを全文出して spool を消す", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();
    expect(texts()).toEqual([]);

    tick();
    appendDelta("m2", 0, "次に進みます。");
    drain();

    // m1 は救済されて全文出る。m2 は final も後続も無いのでまだ出ない
    expect(texts()).toEqual(["確認します。", "ログを見ます。"]);
    expect(fs.readdirSync(spoolDir)).toEqual(["m2.0.json"]);
  });

  it("★ 別セッションのメッセージでは救済しない", () => {
    // spool はグローバルに1ディレクトリで、MessageDisplay は matcher 非対応なので
    // 全セッションで発火する。Claude Code を2枚開くだけでこの条件が揃う。
    // 限定しないと、まだ伸びる途中のメッセージが打ち切られて分断される
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();

    tick();
    appendDelta("other", 0, "別セッションです。", false, "sess-2");
    drain();

    expect(texts()).toEqual([]);
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(true);
  });

  it("★ 文の途中で切れていたら、救済経路では最後の断片を発話しない（続きが来るかもしれない）", () => {
    appendDelta("m1", 0, "Aの一文目。Aの途中");
    drain();

    tick();
    appendDelta("m2", 0, "Bの一文目。Bの二文目。");
    drain();

    // 「Aの途中」は文として閉じていないので落ちる。final 経由なら全部出る
    // （下の「発話の粒度」/messageAssembler.test.ts 参照）
    expect(texts()).toEqual(["Aの一文目。"]);
  });

  it("final 経由（もう続きは来ない）では、末尾が文として閉じていなくても全部出す", () => {
    appendDelta("m1", 0, "Aの一文目。Aの途中", true);
    drain();

    expect(texts()).toEqual(["Aの一文目。", "Aの途中"]);
  });

  it("応答待ち通知の到着でも救済する（ツールが始まる＝メッセージは閉じた）", () => {
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

  it("★ 救済した後に final が届いても、二度発話しない（孤児は次のメッセージを打ち切らず、掃除される）", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain();
    tick();
    appendDelta("m2", 0, "次に進みます。");
    drain();
    expect(texts()).toEqual(["確認します。", "ログを見ます。"]);
    expect(fs.readdirSync(spoolDir)).toEqual(["m2.0.json"]);

    // 救済で m1 の delta ファイルは消えているので、遅れて届いた final は index 0 を
    // 持たない孤児になる。★ 中身のある delta で届く想定にしてある（空 final だと、
    // 孤児が発話されないのは元々当たり前でデータ喪失が構造的に見えないため）
    tick();
    appendDelta("m1", 1, "遅れて届いた分。", true);
    // m2 はまだ伸びている途中（final も後続も無い）
    drain();

    // 孤児の中身は発話されない。m1 の文が二度出ていないこと
    expect(texts()).not.toContain("遅れて届いた分。");
    expect(texts().filter((t) => t === "確認します。")).toHaveLength(1);
    expect(texts().filter((t) => t === "ログを見ます。")).toHaveLength(1);

    // 孤児のせいで m2 が打ち切られていないこと（まだ確定していないのでまだ出ない）
    expect(texts()).not.toContain("次に進みます。");

    // 孤児（m1.1.json）は tombstone によって即座に掃除される（6時間の cleanOrphans を待たない）
    expect(fs.readdirSync(spoolDir)).toEqual(["m2.0.json"]);
  });

  it("★ 欠番があるうちは救済でも publish しない。埋まれば final 経由で全文出る", () => {
    appendDelta("m1", 0, "Aの一文目。");
    appendDelta("m1", 2, "Aの三文目。", true); // index 1 がまだ届いていない
    drain();
    expect(texts()).toEqual([]);

    // m2 の到着で救済条件（hasNewer）は揃うが、欠番があるので publish も削除もしない
    // （方針は「全損維持」）
    tick();
    appendDelta("m2", 0, "Bの一文目。");
    drain();

    expect(texts()).toEqual([]);
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(true);
    expect(fs.existsSync(path.join(spoolDir, "m1.2.json"))).toBe(true);

    // 欠番が埋まれば final 経由で全文出る
    tick();
    appendDelta("m1", 1, "Aの二文目。");
    drain();
    expect(texts()).toEqual(["Aの一文目。", "Aの二文目。", "Aの三文目。"]);
  });

  it("★ index 0 が読めていない（deltas が空）まま救済条件が揃っても spool を消さない", () => {
    // index 0 がそもそも無いので、payload の final:true は連続接頭辞の外で無視される
    appendDelta("m1", 1, "Aの二文目のつもりだった。", true);
    drain();
    expect(texts()).toEqual([]);
    expect(fs.existsSync(path.join(spoolDir, "m1.1.json"))).toBe(true);

    // hasNewer は成立するが、deltas が空なので publish しない
    // （EMFILE 等の一過性の読み取り失敗かもしれず、ファイルを消してはいけない）
    tick();
    appendDelta("m2", 0, "Bです。");
    drain();

    expect(texts()).toEqual([]);
    expect(fs.existsSync(path.join(spoolDir, "m1.1.json"))).toBe(true);
  });

  it("★ tombstone: publish 済みの message_id への遅延 delta は再発話しない", () => {
    appendDelta("m1", 0, "確認します。", true);

    const calls: number[] = [];
    const publish: DrainDeps["publish"] = (entries) => {
      calls.push(entries.length);
      return entries.map((entry, i) => ({ ...entry, seq: i + 1, ts: "2026-08-16T00:00:00.000Z" }));
    };

    drain({ publish });
    expect(calls).toEqual([1]);
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(false);

    // 遅れて m1 が改めて完全な形（index 0 から、final:true 付き）で届く想定
    tick();
    appendDelta("m1", 0, "確認します。", true);
    drain({ publish });

    // tombstone が効いていれば、2回目の drain では publish が呼ばれない
    expect(calls).toEqual([1]);
  });

  it("★ tombstone: removeEntry が失敗しても、次のドレインで再 publish されない", () => {
    appendDelta("m1", 0, "確認します。", true);

    const calls: number[] = [];
    const publish: DrainDeps["publish"] = (entries) => {
      calls.push(entries.length);
      return entries.map((entry, i) => ({ ...entry, seq: i + 1, ts: "2026-08-16T00:00:00.000Z" }));
    };

    // ★ ドレインは1回の呼び出しの中で「進展あり」なら複数パス回るので、1回だけ throw する
    //   mockImplementationOnce だと同じ drain() 呼び出しの2パス目で（tombstone 済みの
    //   エントリの再削除として）呼び直され、そこで実体に委譲されて消えてしまう。
    //   「削除がずっと失敗し続ける」状況を再現するため、drain() 呼び出しの間ずっと throw する
    vi.mocked(removeEntry).mockImplementation(() => {
      throw new Error("EACCES");
    });

    // removeEntry が失敗してもドレイン全体は止まらない
    expect(() => drain({ publish })).not.toThrow();
    expect(calls).toEqual([1]);
    // 削除に失敗し続けたので spool ファイルはまだ残っている
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(true);

    // 次のドレインでは削除は成功するが、tombstone が効いて publish は呼ばれない
    vi.mocked(removeEntry).mockImplementation(actualSpoolRef.current!.removeEntry);
    drain({ publish });
    expect(calls).toEqual([1]);
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(false);
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

  it("★ 廃止した進捗サイドカーの残骸（prompt-<…>.progress.json）は専用ガード無しでも即座に片付く", () => {
    // spool.ts の PROGRESS_SUFFIX ガードを外した（実害が「掃除を孤児掃除の6時間まで遅らせて
    // いるだけ」だったため）。この残骸は prompt-*.json と同じ形なので通常の prompt entry
    // として拾われるが、formatPromptEvent が未知 payload（hook_event_name が無い）に [] を
    // 返すため、processPrompt の「発話ゼロなら即削除」分岐に落ちる。ガード除去で壊れないことを
    // ここで示す
    writePrompt("x.progress", { emitted: 1 });
    drain();
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

/**
 * 長いメッセージを要約してから読み上げる経路（issue #31）。`summarize`（drain() の既定は素通し）を
 * 差し替えて確認する。無限ループ防止の第2層（summarizerSessionIds）もここでまとめて見る。
 */
/**
 * [#33] MessageDisplay と PreToolUse は別プロセスとして同時に走るので、どちらが先に spool へ
 * 着くかに保証が無い。prompt が先に着くと「質問を読み上げてから、その質問に至る説明を
 * 読み上げる」逆転が起きる（実機で観測、短い本文で −316ms）。
 *
 * worker.ts の `hoistMessagesBeforePrompt` が、同一セッション・同一 prompt_id の本文を
 * prompt の前へ引き上げて順序を戻す。
 */
describe("★ [#33] prompt が本文を追い越して spool へ着いたとき", () => {
  const question = {
    session_id: "sess-1",
    prompt_id: "p1",
    hook_event_name: "PreToolUse",
    tool_name: "AskUserQuestion",
    tool_input: { questions: [{ question: "次は何をしますか？", options: [{ label: "進める" }] }] },
  };

  it("final:true の本文でも、本文 → 質問の順で発話する", () => {
    // 逆転の実測ケース: 本文は単一 delta（index 0 に final:true）で、PreToolUse の方が先に着いた
    writePrompt("q", question);
    tick();
    appendDelta("m1", 0, "監視を開始しました。", true);
    drain();

    expect(texts()).toEqual(["監視を開始しました。", "次は何をしますか？", "選択肢は、進める。"]);
  });

  it("final がまだ来ていなくても、prompt が後ろに回るので救済経路が本文を先に出す", () => {
    writePrompt("q", question);
    tick();
    appendDelta("m1", 0, "監視を開始しました。");
    drain();

    expect(texts()).toEqual(["監視を開始しました。", "次は何をしますか？", "選択肢は、進める。"]);
  });

  it("★ prompt_id が違う本文は引き上げない（別ターンの本文を巻き込まない）", () => {
    writePrompt("q", question);
    tick();
    appendDelta("m1", 0, "別ターンの本文です。", true, "sess-1", "p2");
    drain();

    expect(texts()).toEqual(["次は何をしますか？", "選択肢は、進める。", "別ターンの本文です。"]);
  });

  it("★ session_id が違う本文は引き上げない（Claude Code を2枚開いたとき）", () => {
    writePrompt("q", question);
    tick();
    appendDelta("m1", 0, "別セッションの本文です。", true, "sess-2");
    drain();

    expect(texts()).toEqual(["次は何をしますか？", "選択肢は、進める。", "別セッションの本文です。"]);
  });

  it("同一 prompt_id の本文が複数あれば、到着順のまま全部を prompt の前に出す", () => {
    writePrompt("q", question);
    tick();
    appendDelta("m1", 0, "1つめの本文です。", true);
    tick();
    appendDelta("m2", 0, "2つめの本文です。", true);
    drain();

    expect(texts()).toEqual(["1つめの本文です。", "2つめの本文です。", "次は何をしますか？", "選択肢は、進める。"]);
  });

  it("★ 同じ prompt_id の prompt が2つ追い越しても、本文を二重に発話しない", () => {
    // AskUserQuestion では PreToolUse の直後に同じ prompt_id の Notification も発火する。
    // 引き上げ済みを覚えていないと、同じ本文が2つの prompt の前にそれぞれ積まれる
    writePrompt("a-pre", question);
    tick();
    writePrompt("b-notify", {
      session_id: "sess-1",
      prompt_id: "p1",
      hook_event_name: "Notification",
      message: "Claude needs your permission",
    });
    tick();
    appendDelta("m1", 0, "監視を開始しました。", true);
    drain();

    expect(texts().filter((t) => t === "監視を開始しました。")).toHaveLength(1);
    // 本文が先。Notification は PreToolUse とのペア判定で捨てられる
    expect(texts()).toEqual(["監視を開始しました。", "次は何をしますか？", "選択肢は、進める。"]);
  });

  it("prompt が本文より後に着いた通常のケースは、並びを変えない", () => {
    appendDelta("m1", 0, "監視を開始しました。", true);
    tick();
    writePrompt("q", question);
    drain();

    expect(texts()).toEqual(["監視を開始しました。", "次は何をしますか？", "選択肢は、進める。"]);
  });
});

describe("AI要約", () => {
  it("要約が publish の手前で挟まり、要約後の文が記録される", () => {
    appendDelta("m1", 0, "とても長いメッセージです。", true);
    drain({ summarize: () => "要約結果。" });
    expect(texts()).toEqual(["要約結果。"]);
  });

  it('★ 要約に渡すテキストは文を "\\n" で連結した全文（join("") にすると文の切れ目が消える）', () => {
    appendDelta("m1", 0, "あ。い。う。", true);
    let received = "";
    drain({
      summarize: (text) => {
        received = text;
        return text;
      },
    });
    expect(received).toBe("あ。\nい。\nう。");
  });

  it("要約結果が複数文なら文に割り直して publish される（seq が連続・ts が同値＝メッセージ単位の契約が保たれる）", () => {
    appendDelta("m1", 0, "長いメッセージ。", true);
    drain({ summarize: () => "要約その1。要約その2。要約その3。" });

    const rows = records();
    expect(rows.map((r) => r.text)).toEqual(["要約その1。", "要約その2。", "要約その3。"]);
    expect(rows.map((r) => r.seq)).toEqual([1, 2, 3]);
    expect(new Set(rows.map((r) => r.ts)).size).toBe(1);
  });

  it("★ 要約結果が空になったら原文にフォールバックする（無言の欠落を防ぐ回帰テスト。仕様6）", () => {
    appendDelta("m1", 0, "確認します。ログを見ます。", true);
    // 要約 CLI がコードブロックの記号だけを返したケースを模す。cleanTextForSpeech で
    // 中身ごと削られて空文字になり、splitIntoSentences の結果が0本になる
    drain({ summarize: () => "```silent```" });
    expect(texts()).toEqual(["確認します。", "ログを見ます。"]);
  });

  it("★ 未閉じのコードフェンスを含む要約は、コード片を読み上げに漏らさない（A2: toSpeechSentences への一本化）", () => {
    appendDelta("m1", 0, "とても長いメッセージです。", true);
    // 要約結果に未閉じのコードフェンスが含まれるケース。旧実装（splitIntoSentences +
    // cleanTextForSpeech の直呼び）は truncateAtUnstableTail を通らないので、この
    // "const x = 1;" がそのまま publish されてしまっていた
    drain({ summarize: () => "変更しました。\n```\nconst x = 1;" });
    expect(texts()).toEqual(["変更しました。"]);
    expect(texts().join("")).not.toContain("const x");
  });

  it("要約が原文をそのまま返したら、元の文の並びがそのまま出る", () => {
    appendDelta("m1", 0, "あ。い。う。", true);
    drain({ summarize: (text) => text });
    expect(texts()).toEqual(["あ。", "い。", "う。"]);
  });

  it("★ kind: prompt は要約されない（summarize が呼ばれないことまで見る）", () => {
    let summarizeCalls = 0;
    writePrompt("n", { session_id: "sess-1", hook_event_name: "Notification", message: "許可してください。" });
    drain({
      summarize: (text) => {
        summarizeCalls++;
        return text;
      },
    });
    expect(summarizeCalls).toBe(0);
    expect(texts()).toEqual(["許可してください。"]);
  });

  it("★ 救済経路（final を待たず hasNewer で publish される経路）では要約されない（A3: summarize が呼ばれないことまで見る）", () => {
    let summarizeCalls = 0;
    const summarize: DrainDeps["summarize"] = (text) => {
      summarizeCalls++;
      return text;
    };

    // final なし。次のメッセージの到着で救済されるまで待つ
    appendDelta("m1", 0, "確認します。ログを見ます。");
    drain({ summarize });
    expect(summarizeCalls).toBe(0);

    tick();
    appendDelta("m2", 0, "次に進みます。");
    drain({ summarize });

    // m1 は救済されて全文出るが、打ち切りに数十秒かけて要約する筋が無いので summarize は呼ばれない
    expect(texts()).toEqual(["確認します。", "ログを見ます。"]);
    expect(summarizeCalls).toBe(0);
  });

  it("★ 第2層: 要約セッションの session_id を持つ delta は発話されず spool から消える（message）", () => {
    const statePath = path.join(dir, "speak.state.json");
    const state = emptyWorkerState();
    addSummarizerSession(state, "summarizer-sess");
    writeWorkerState(statePath, state);

    appendDelta("m1", 0, "要約プロセス自身の出力です。", true, "summarizer-sess");
    drain();

    expect(texts()).toEqual([]);
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(false);
  });

  it("★ 第2層: 要約セッションの session_id を持つ応答待ち通知も発話されず spool から消える（prompt）", () => {
    const statePath = path.join(dir, "speak.state.json");
    const state = emptyWorkerState();
    addSummarizerSession(state, "summarizer-sess");
    writeWorkerState(statePath, state);

    writePrompt("s", {
      session_id: "summarizer-sess",
      hook_event_name: "Notification",
      message: "要約プロセスの通知です。",
    });
    drain();

    expect(texts()).toEqual([]);
    expect(fs.readdirSync(spoolDir)).toEqual([]);
  });

  it("★ 第2層の永続化: registerSessionId が呼ばれた時点で speak.state.json に既に書かれている", () => {
    appendDelta("m1", 0, "長いメッセージ。", true);

    let sawDuringCall = false;
    drain({
      summarize: (text, registerSessionId) => {
        registerSessionId("sess-mid-call");
        // summarize の中から state ファイルを直接読み、execFileSync の前に永続化される
        // という契約（summarizer/types.ts の Summarize）が守られていることを確認する
        const state = readWorkerState(path.join(dir, "speak.state.json"));
        sawDuringCall = state.summarizerSessionIds.includes("sess-mid-call");
        return text;
      },
    });

    expect(sawDuringCall).toBe(true);
  });

  it("★ 第2層のレジストリはプロセスを跨いで効く", () => {
    appendDelta("m1", 0, "長いメッセージ。", true);
    drain({
      summarize: (text, registerSessionId) => {
        registerSessionId("summarizer-sess");
        return text;
      },
    });
    expect(texts()).toEqual(["長いメッセージ。"]);

    tick();
    appendDelta("m2", 0, "要約プロセス自身の出力です。", true, "summarizer-sess");
    // drain() のたびに state をディスクから読み直しているので、別プロセスと同じ条件
    // （「★ 抑制状態はプロセスを跨いで効く」と同じ形）
    drain();

    expect(texts()).not.toContain("要約プロセス自身の出力です。");
    expect(fs.existsSync(path.join(spoolDir, "m2.0.json"))).toBe(false);
  });

  it("★ A4: 要約を試みた message_id は次のドレインでスキップされる（再要約を高々1回に抑える）", () => {
    appendDelta("m1", 0, "長いメッセージ。", true);

    let summarizeCalls = 0;
    drain({
      summarize: (text, registerSessionId) => {
        summarizeCalls++;
        registerSessionId("summarizer-sess-1");
        return text; // 要約は不採用（原文フォールバック）だが、試みたこと自体は記録される
      },
    });
    expect(summarizeCalls).toBe(1);
    expect(texts()).toEqual(["長いメッセージ。"]);

    // ★ 「要約中に親プロセスが落ちて tombstone が永続化されなかった」状況を模す。
    //   summaryAttemptedMessageIds は registerSessionId のコールバックの中で
    //   （summarize 呼び出し時点で）既に永続化されているので、tombstone だけを
    //   意図的に外した state を用意する
    const statePath = path.join(dir, "speak.state.json");
    const state = readWorkerState(statePath);
    state.publishedMessageIds = state.publishedMessageIds.filter((id) => id !== "m1");
    writeWorkerState(statePath, state);

    tick();
    appendDelta("m1", 0, "長いメッセージ。", true); // 同じ message_id が再度届く
    drain({
      summarize: (text, registerSessionId) => {
        summarizeCalls++;
        registerSessionId("summarizer-sess-2");
        return text;
      },
    });

    // summarize は再度呼ばれない（summaryAttemptedMessageIds に載っているため）が、
    // 発話そのものは失われず、原文がそのまま publish される
    expect(summarizeCalls).toBe(1);
    expect(texts().filter((t) => t === "長いメッセージ。")).toHaveLength(2);
  });
});

/**
 * A4（issue #38 レビュー）: state の永続化が publish の後で失敗しても、二重発話（次の
 * ドレインでの再 publish）を防ぐために spool 削除まで必ず到達すること。
 */
describe("state の永続化が失敗したときの安全側の挙動（A4）", () => {
  it("★ state を永続化できなくても drainSpool は落ちず、publish 済みメッセージの spool は消える", () => {
    appendDelta("m1", 0, "確認します。", true);

    // 親ディレクトリごと存在しないパスにして、writeWorkerState を自然に失敗させる
    // （summaryPipeline.test.ts の brokenLogPath と同じ手法。vi.mock は使わない）
    const brokenStatePath = path.join(dir, "no-such-dir", "nested", "speak.state.json");

    expect(() => drain({ workerStatePath: brokenStatePath })).not.toThrow();
    // publish は成功している（tombstone の永続化とは独立）
    expect(texts()).toEqual(["確認します。"]);
    // tombstone は永続化できていないはずだが、tryRemoveEntry には到達しているので
    // spool ファイルは消えている（tombstone と spool 削除のどちらか一方が成功すればよい）
    expect(fs.existsSync(path.join(spoolDir, "m1.0.json"))).toBe(false);
  });
});

/**
 * E1（issue #38 レビュー）: 要約が効いたときは、原文（要約前の全文）由来の感情を全文で共有する。
 * 要約が効かなかったときは、従来どおり文ごとに判定する。
 */
describe("感情判定（E1）", () => {
  it("★ 要約が効いたとき、publish される全文が同じ emotion になる（原文全体で判定する）", () => {
    appendDelta("m1", 0, "元のメッセージです。", true);
    drain({
      summarize: () => "要約その1！要約その2？要約その3。",
      // 要約後の文には「元の」が含まれない。文ごとに判定していたら neutral になるはずの値
      classify: (text) => (text.includes("元の") ? "happy" : "neutral"),
    });

    const rows = records();
    expect(rows.map((r) => r.text)).toEqual(["要約その1！", "要約その2？", "要約その3。"]);
    // 原文（sentences.join）で判定した1つの emotion が全文で共有されている
    expect(rows.map((r) => r.emotion)).toEqual(["happy", "happy", "happy"]);
  });

  it("要約が効かなかったとき（原文フォールバック）は、従来どおり文ごとに emotion が判定される", () => {
    appendDelta("m1", 0, "うれしいです。むずかしいです。", true);
    drain({
      summarize: (text) => text, // 要約が効かない
      classify: (text) => (text.includes("うれしい") ? "happy" : "neutral"),
    });

    const rows = records();
    expect(rows.map((r) => r.text)).toEqual(["うれしいです。", "むずかしいです。"]);
    expect(rows.map((r) => r.emotion)).toEqual(["happy", "neutral"]);
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
