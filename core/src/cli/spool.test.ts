import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { cleanOrphans, readMessage, readPromptPayload, removeEntry, scanSpool } from "./spool";

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

/**
 * hook が置く形（`<message_id>.<index>.json`、1ファイル1 JSON）を再現する。
 * 返すのは書いたファイルパスの配列（渡した payload の順）。
 */
function writeMessage(messageId: string, payloads: { index: number }[]): string[] {
  return payloads.map((p) => {
    const filePath = path.join(spoolDir, `${messageId}.${p.index}.json`);
    fs.writeFileSync(filePath, JSON.stringify(p));
    return filePath;
  });
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

function setMtime(filePath: string, ms: number): void {
  const t = new Date(ms);
  fs.utimesSync(filePath, t, t);
}

/**
 * 廃止した進捗サイドカー（#30）の残骸を置く。既存インストールの spool に残っているもの。
 * ワーカーはもう書かない。専用の読み側ガード（`PROGRESS_SUFFIX`）は外した — 実害が
 * 「掃除を孤児掃除の6時間まで遅らせているだけ」だったため（下のテスト参照）。
 */
function writeStaleProgress(fileName: string): string {
  const filePath = path.join(spoolDir, fileName);
  fs.writeFileSync(filePath, JSON.stringify({ emitted: 1 }));
  return filePath;
}

function messageEntry(kind: "message" | "prompt" = "message") {
  const entries = scanSpool(spoolDir);
  const entry = entries.find((e) => e.kind === kind);
  if (!entry) throw new Error(`no ${kind} entry`);
  return entry;
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

  it("`<message_id>.progress.json`（メッセージ側の旧サイドカー）は走査対象にしない", () => {
    // メッセージの delta ファイル名パターン（<message_id>.<index>.json）にも prompt-*.json にも
    // 一致しないので、専用ガードの有無に関わらず元から無視される
    writeMessage("m1", [delta(0, "あ。")]);
    writeStaleProgress("m1.progress.json");

    const entries = scanSpool(spoolDir);
    expect(entries).toHaveLength(1);
    expect(entries[0]?.kind).toBe("message");
  });

  it("`prompt-<…>.progress.json`（応答待ち側の旧サイドカー）は prompt entry として拾われる", () => {
    // 以前は専用ガード（PROGRESS_SUFFIX）でここだけ弾いていたが、外しても実害が無いと分かった
    // ので削除した。formatPromptEvent が未知 payload に [] を返すため、worker.ts の
    // processPrompt が発話ゼロのまま即削除する（worker.test.ts で確認）。回収は通常の
    // 削除経路か、ドレインが起きない場合の保険として孤児掃除（既定6時間）が引き取る
    writeMessage("m1", [delta(0, "あ。")]);
    writeStaleProgress("prompt-x.progress.json");

    const entries = scanSpool(spoolDir);
    expect(entries).toHaveLength(2);
    expect(entries.map((e) => e.kind).sort()).toEqual(["message", "prompt"]);
  });

  it("関係ないファイルは無視する", () => {
    fs.writeFileSync(path.join(spoolDir, "README.txt"), "x");
    fs.mkdirSync(path.join(spoolDir, "subdir"));
    expect(scanSpool(spoolDir)).toEqual([]);
  });

  it("delta の tmp ファイル（rename 前）は走査対象にならない", () => {
    // `<message_id>.<index>.json.tmp` は末尾が `.json` にならないので、classify のどの
    // 分岐にも一致せず無視される（rename が終わるまで自然に隠れる）
    writeMessage("m1", [delta(0, "あ。")]);
    fs.writeFileSync(path.join(spoolDir, "m1.1.json.tmp"), "{}");

    const entries = scanSpool(spoolDir);
    expect(entries).toHaveLength(1);
    const entry = entries[0]!;
    if (entry.kind !== "message") throw new Error("unreachable");
    expect(entry.filePaths).toHaveLength(1);
  });

  it("メッセージのファイル名（<message_id>.<index>.json）から message_id を決める", () => {
    writeMessage("a94cd2c5", [delta(0, "あ。")]);
    const entry = messageEntry();
    if (entry.kind !== "message") throw new Error("unreachable");
    expect(entry.messageId).toBe("a94cd2c5");
  });

  it("同じ message_id の delta ファイルを1エントリにまとめ、index 昇順に並べる", () => {
    // わざと届く順を index の昇順とずらして置く
    writeMessage("m1", [delta(1, "います。"), delta(0, "確認して"), delta(2, "。", true)]);

    const entry = messageEntry();
    if (entry.kind !== "message") throw new Error("unreachable");
    expect(entry.filePaths.map((f) => path.basename(f))).toEqual(["m1.0.json", "m1.1.json", "m1.2.json"]);
  });

  it("到着順（index 0 ファイルの作成順）に並ぶ", () => {
    // 名前順に並べると first → prompt-third → second になるので、
    // 作成順で並んでいることがこの並びで分かる
    writeMessage("first", [delta(0, "あ。")]);
    tick();
    writeMessage("second", [delta(0, "い。")]);
    tick();
    fs.writeFileSync(path.join(spoolDir, "prompt-third.json"), "{}");

    expect(scanSpool(spoolDir).map((e) => (e.kind === "message" ? e.messageId : path.basename(e.filePath)))).toEqual([
      "first",
      "second",
      "prompt-third.json",
    ]);
  });

  it("後から届いた delta（index>0）のファイルの方が新しくても、メッセージの到着順は index 0 の作成時刻で決まる", () => {
    // ★ 「グループの最新ファイル」で並べるとここが壊れる。final:true は大きく遅れて届くため、
    //   先行メッセージの index>0 のファイルが後発メッセージより新しく作られるのが普通に起きる
    writeMessage("first", [delta(0, "あ。")]);
    tick();
    writeMessage("second", [delta(0, "い。")]);
    tick();
    // 大きく遅れて final が届いた想定。このファイルは全体の中で一番新しく作られる
    writeMessage("first", [delta(1, "う。", true)]);

    expect(scanSpool(spoolDir).map((e) => (e.kind === "message" ? e.messageId : "?"))).toEqual(["first", "second"]);
  });

  it("index 0 のファイルが無ければ、手元にある中で最小の到着順（最初に作られたもの）を使う", () => {
    // index 0 が欠けている状況（通常は起きないが、フォールバックの確認）
    fs.writeFileSync(path.join(spoolDir, "m1.2.json"), JSON.stringify(delta(2, "う。")));
    tick();
    fs.writeFileSync(path.join(spoolDir, "m1.5.json"), JSON.stringify(delta(5, "お。", true)));
    tick();
    writeMessage("m2", [delta(0, "い。")]); // 後から届いた別メッセージ

    expect(scanSpool(spoolDir).map((e) => (e.kind === "message" ? e.messageId : "?"))).toEqual(["m1", "m2"]);
  });
});

describe("readMessage", () => {
  it("index 順に delta を並べる", () => {
    const filePaths = writeMessage("m1", [delta(1, "います。"), delta(0, "確認して")]);
    const content = readMessage(filePaths);
    expect(content.deltas).toEqual(["確認して", "います。"]);
    expect(content.final).toBe(false);
  });

  it("final:true を拾う", () => {
    const filePaths = writeMessage("m1", [delta(0, "あ。"), delta(1, "い。", true)]);
    expect(readMessage(filePaths).final).toBe(true);
  });

  it("payload から sessionId / turnId / messageId を取る", () => {
    const filePaths = writeMessage("m1", [delta(0, "あ。")]);
    const content = readMessage(filePaths);
    expect(content.sessionId).toBe("sess-1");
    expect(content.turnId).toBe("turn-1");
    expect(content.messageId).toBe("m1");
  });

  it("index に欠番があればそこで打ち切る（歯抜けを繋いで文を壊さない）", () => {
    const filePaths = writeMessage("m1", [delta(0, "あ。"), delta(2, "う。", true)]);
    const content = readMessage(filePaths);
    expect(content.deltas).toEqual(["あ。"]);
    expect(content.final).toBe(false);
  });

  it("index 0 が無ければ何も読まない", () => {
    const filePaths = writeMessage("m1", [delta(1, "い。")]);
    expect(readMessage(filePaths).deltas).toEqual([]);
  });

  it("★ hasGap: 欠番があれば true（欠番より後ろに読めたファイルが残っている）", () => {
    const filePaths = writeMessage("m1", [delta(0, "あ。"), delta(2, "う。", true)]);
    expect(readMessage(filePaths).hasGap).toBe(true);
  });

  it("★ hasGap: 欠番が無く全部連続していれば false", () => {
    const filePaths = writeMessage("m1", [delta(0, "あ。"), delta(1, "い。", true)]);
    expect(readMessage(filePaths).hasGap).toBe(false);
  });

  it("★ hasGap: index 0 が無ければ true（読めた分はあるが連続接頭辞には入らない）", () => {
    const filePaths = writeMessage("m1", [delta(1, "い。")]);
    expect(readMessage(filePaths).hasGap).toBe(true);
  });

  it("★ hasGap: 何も読めなければ false（欠番の判定材料が無い）", () => {
    expect(readMessage([path.join(spoolDir, "nope.0.json")]).hasGap).toBe(false);
  });

  it("パース不能なファイルは捨てて、読めたファイルだけ使う（rename 直前の書き込み途中を掴んだ可能性）", () => {
    const f0 = path.join(spoolDir, "m1.0.json");
    fs.writeFileSync(f0, JSON.stringify(delta(0, "あ。")));
    const f1 = path.join(spoolDir, "m1.1.json");
    fs.writeFileSync(f1, '{"index":1,"delta":"い'); // 壊れた JSON
    expect(readMessage([f0, f1]).deltas).toEqual(["あ。"]);
  });

  it("readMessage は payload の index を信用する（ファイル名はグルーピングにしか使わない）。同じ index が複数あれば後勝ち", () => {
    const f0 = path.join(spoolDir, "m1.0.json");
    fs.writeFileSync(f0, JSON.stringify(delta(0, "旧")));
    // ファイル名は index 1 だが、中身の index は 0（想定外の状況への備え）
    const f1 = path.join(spoolDir, "m1.1.json");
    fs.writeFileSync(f1, JSON.stringify(delta(0, "新")));
    expect(readMessage([f0, f1]).deltas).toEqual(["新"]);
  });

  it("ファイルが無ければ空", () => {
    expect(readMessage([path.join(spoolDir, "nope.0.json")]).deltas).toEqual([]);
  });

  it("空配列なら空", () => {
    expect(readMessage([]).deltas).toEqual([]);
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

describe("removeEntry", () => {
  it("メッセージは全 delta ファイルを消す", () => {
    writeMessage("m1", [delta(0, "あ。"), delta(1, "い。", true)]);
    const entry = messageEntry();
    if (entry.kind !== "message") throw new Error("unreachable");

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
  it("無活動が閾値を超えたメッセージを、delta ファイルごと消す", () => {
    const [staleFile] = writeMessage("stale", [delta(0, "あ。")]);
    writeMessage("fresh", [delta(0, "い。")]);
    setMtime(staleFile!, Date.now() - 10 * 60 * 60 * 1000);

    expect(cleanOrphans(spoolDir, 6 * 60 * 60 * 1000)).toBe(1);
    expect(fs.readdirSync(spoolDir)).toEqual(["fresh.0.json"]);
  });

  it("進行中のメッセージは消さない", () => {
    writeMessage("m1", [delta(0, "あ。")]);
    expect(cleanOrphans(spoolDir, 6 * 60 * 60 * 1000)).toBe(0);
  });

  it("★ メッセージ単位で判定する（古い index 0 と新しい index 1 が並んでいても、どちらも消えない）", () => {
    // 1 delta 1 ファイルにすると、各ファイルの mtime は書かれた瞬間で止まる。ファイル単位で
    // 無活動判定すると、進行中メッセージの古い delta だけが消えて index に欠番ができ、
    // そのメッセージが永久に発話されなくなる（CLAUDE.md「絶対に守ること」5 の落とし穴）。
    const [zeroFile] = writeMessage("m1", [delta(0, "あ。")]);
    setMtime(zeroFile!, Date.now() - 10 * 60 * 60 * 1000); // 単体で見れば閾値を超える古さ

    // index 1 はついさっき届いた最新の delta。メッセージ自体は進行中
    writeMessage("m1", [delta(1, "い。")]);

    expect(cleanOrphans(spoolDir, 6 * 60 * 60 * 1000)).toBe(0);
    expect(fs.readdirSync(spoolDir).sort()).toEqual(["m1.0.json", "m1.1.json"]);
  });

  it("★ 進捗サイドカーの残骸は、メッセージとは独立にファイル単位で回収する", () => {
    // #30 でサイドカーは廃止した。既存インストールに残った分は誰も更新しないので、
    // グループに含めずファイル単位で古さを見る（含めると、進行中メッセージの delta を
    // 古いサイドカーが道連れにする／その逆が起きる）
    const stale = writeStaleProgress("m1.progress.json");
    setMtime(stale, Date.now() - 10 * 60 * 60 * 1000);
    writeMessage("m1", [delta(0, "あ。")]); // 同じ message_id の進行中メッセージ

    expect(cleanOrphans(spoolDir, 6 * 60 * 60 * 1000)).toBe(1);
    expect(fs.readdirSync(spoolDir)).toEqual(["m1.0.json"]);
  });

  it("ディレクトリが無くても落ちない", () => {
    expect(cleanOrphans(path.join(spoolDir, "nope"), 1000)).toBe(0);
  });
});
