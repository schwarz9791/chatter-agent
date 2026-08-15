import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createSpeechTail } from "./speechTail";

let dir: string;
let logPath: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-tail-"));
  logPath = path.join(dir, "speech.jsonl");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

function line(seq: number, text = `文${seq}。`): string {
  return JSON.stringify({ seq, ts: "2026-08-15T00:00:00.000Z", kind: "assistant", text });
}

function append(...seqs: number[]): void {
  fs.appendFileSync(logPath, seqs.map((s) => line(s)).join("\n") + "\n");
}

describe("readNew", () => {
  it("ファイルがまだ無くても落ちない", () => {
    expect(createSpeechTail(logPath).readNew()).toEqual([]);
  });

  it("増えた行だけを返す", () => {
    const tail = createSpeechTail(logPath);
    append(1, 2);
    expect(tail.readNew()).toEqual([line(1), line(2)]);

    append(3);
    expect(tail.readNew()).toEqual([line(3)]);
  });

  it("変化が無ければ空", () => {
    const tail = createSpeechTail(logPath);
    append(1);
    tail.readNew();
    expect(tail.readNew()).toEqual([]);
  });

  it("追記の途中に当たっても、完結した行だけを返す", () => {
    const tail = createSpeechTail(logPath);
    fs.writeFileSync(logPath, line(1) + "\n" + line(2).slice(0, 20));

    expect(tail.readNew()).toEqual([line(1)]);

    // 残りが書き終わったら次で拾う
    fs.appendFileSync(logPath, line(2).slice(20) + "\n");
    expect(tail.readNew()).toEqual([line(2)]);
  });

  it("行が1本も完結していなければ何も返さない", () => {
    const tail = createSpeechTail(logPath);
    fs.writeFileSync(logPath, '{"seq":1,"te');
    expect(tail.readNew()).toEqual([]);
    expect(tail.position()).toBe(0);
  });

  it("マルチバイト文字が読み取り境界に来ても壊れない", () => {
    const tail = createSpeechTail(logPath);
    append(1);
    expect(tail.readNew()).toEqual([line(1)]);

    append(2);
    expect(tail.readNew()).toEqual([line(2)]);
    // 位置がバイト数で進んでいること（文字数で進めるとここがずれる）
    expect(tail.position()).toBe(fs.statSync(logPath).size);
  });

  it("seekToEnd で溜まっている分を飛ばす", () => {
    append(1, 2);
    const tail = createSpeechTail(logPath);
    tail.seekToEnd();
    expect(tail.readNew()).toEqual([]);

    append(3);
    expect(tail.readNew()).toEqual([line(3)]);
  });
});

describe("ローテートへの追従（設計書 §6）", () => {
  it("サイズが読み取り位置より小さくなったら世代交代と判断して先頭から読み直す", () => {
    const tail = createSpeechTail(logPath);
    append(1, 2, 3);
    expect(tail.readNew()).toHaveLength(3);

    // speech.jsonl → speech.1.jsonl に退避され、新しいファイルが開かれる
    fs.renameSync(logPath, path.join(dir, "speech.1.jsonl"));
    append(4);

    expect(tail.readNew()).toEqual([line(4)]);
  });

  it("ローテート後も配信が続く", () => {
    const tail = createSpeechTail(logPath);
    append(1);
    tail.readNew();

    fs.renameSync(logPath, path.join(dir, "speech.1.jsonl"));
    append(2);
    expect(tail.readNew()).toEqual([line(2)]);

    append(3);
    expect(tail.readNew()).toEqual([line(3)]);
  });

  it("新世代がたまたま同じサイズに達しても取りこぼさない", () => {
    // ★ サイズ比較だけだとここが抜ける。line(1) と line(2) はバイト数が同じ
    const tail = createSpeechTail(logPath);
    append(1);
    expect(tail.readNew()).toEqual([line(1)]);
    const sizeBefore = fs.statSync(logPath).size;

    fs.renameSync(logPath, path.join(dir, "speech.1.jsonl"));
    append(2);
    expect(fs.statSync(logPath).size).toBe(sizeBefore); // 同サイズであることを確認したうえで

    expect(tail.readNew()).toEqual([line(2)]);
  });

  it("ローテート中に一時的にファイルが見えなくても、次で先頭から読み直す", () => {
    const tail = createSpeechTail(logPath);
    append(1);
    tail.readNew();

    fs.renameSync(logPath, path.join(dir, "speech.1.jsonl"));
    expect(tail.readNew()).toEqual([]); // 新しいファイルはまだ無い

    append(2);
    expect(tail.readNew()).toEqual([line(2)]);
  });

  it("★ 読み取りとローテートの間に書かれた行を落とさない", () => {
    const tail = createSpeechTail(logPath);
    append(1);
    expect(tail.readNew()).toEqual([line(1)]);

    // ここで server が読む前に 2,3 が書かれ、そのままローテートされる
    append(2, 3);
    fs.renameSync(logPath, path.join(dir, "speech.1.jsonl"));
    append(4);

    // 退避された世代の未読分 → 新世代 の順で、欠けずに届く
    expect(tail.readNew()).toEqual([line(2), line(3), line(4)]);
  });

  it("ローテート中に見えなくなった隙に書かれた行も落とさない", () => {
    const tail = createSpeechTail(logPath);
    append(1);
    tail.readNew();

    append(2);
    fs.renameSync(logPath, path.join(dir, "speech.1.jsonl"));
    expect(tail.readNew()).toEqual([]); // 新しいファイルがまだ無い瞬間

    append(3);
    expect(tail.readNew()).toEqual([line(2), line(3)]);
  });

  it("2世代以上が一度に流れたら、現世代だけを配信する（誤った世代を配信しない）", () => {
    const tail = createSpeechTail(logPath);
    append(1);
    tail.readNew();

    // 1回目のローテート
    append(2);
    fs.renameSync(logPath, path.join(dir, "speech.1.jsonl"));
    // 2回目のローテート（speech.1 → speech.2 に繰り下がる）
    append(3);
    fs.renameSync(path.join(dir, "speech.1.jsonl"), path.join(dir, "speech.2.jsonl"));
    fs.renameSync(logPath, path.join(dir, "speech.1.jsonl"));
    append(4);

    // speech.1.jsonl は「読んでいた世代」ではないので、位置を当てにできず読まない。
    // 欠落は seq の飛びとしてクライアントに見え、?since= で埋め直せる。
    // ここに至るには2回の読み取りの間に上限サイズの2倍が書かれる必要がある
    expect(tail.readNew()).toEqual([line(4)]);
  });
});

describe("backfill", () => {
  /** server の起動と同じ状態にする（既に手元にある分は「配信済み」として扱われる） */
  function started() {
    const tail = createSpeechTail(logPath);
    tail.seekToEnd();
    return tail;
  }

  it("seq > since の行を返す", () => {
    append(1, 2, 3);
    expect(started().backfill(1)).toEqual([line(2), line(3)]);
  });

  it("since=0 なら現世代の全部", () => {
    append(1, 2);
    expect(started().backfill(0)).toEqual([line(1), line(2)]);
  });

  it("追いついていれば空", () => {
    append(1, 2);
    expect(started().backfill(2)).toEqual([]);
  });

  it("現世代より古い分は返せない（seq の飛びがクライアントへの合図になる）", () => {
    append(5, 6); // ローテート済みで 1〜4 は前世代にある想定
    expect(started().backfill(1)).toEqual([line(5), line(6)]);
  });

  it("ファイルが無ければ空", () => {
    expect(started().backfill(0)).toEqual([]);
  });

  it("壊れた行は配信対象にしない", () => {
    fs.writeFileSync(logPath, "{ broken\n" + line(2) + "\n");
    expect(started().backfill(0)).toEqual([line(2)]);
  });

  it("読み取り位置を動かさない（配信中の接続に影響しない）", () => {
    const tail = createSpeechTail(logPath);
    append(1, 2);
    tail.readNew();
    const before = tail.position();

    tail.backfill(0);
    expect(tail.position()).toBe(before);
  });

  it("★ まだ配信していない行は返さない（二重配信を防ぐ）", () => {
    // ws は connection を発火する前にクライアントを wss.clients に入れるので、未配信の行を
    // backfill に含めると、直後の watcher 発火と合わせて新規クライアントだけが二重に受け取る
    const tail = createSpeechTail(logPath);
    append(1, 2);
    tail.readNew();
    append(3, 4);

    expect(tail.backfill(1)).toEqual([line(2)]);

    // 未配信だった分は readNew（= broadcast）で届く
    expect(tail.readNew()).toEqual([line(3), line(4)]);
    expect(tail.backfill(1)).toEqual([line(2), line(3), line(4)]);
  });
});

/**
 * レビュー #9 の回帰テスト。
 *
 * `speechLog` は追記が途中で切れた断片を**断片のまま隔離する**仕様なので、ログには
 * 不正な UTF-8 が残りうる。消費バイト数を文字列から数えていると、U+FFFD に化けた分だけ
 * `position` が本来より進む。
 */
describe("不正な UTF-8 があっても読み取り位置がずれない", () => {
  /** 追記が途中で切れて、マルチバイトの途中で終わった断片 */
  const TORN = Buffer.from([0x7b, 0x22, 0x74, 0xe3, 0x81]);

  it("position がファイルサイズを超えない", () => {
    fs.writeFileSync(logPath, Buffer.concat([TORN, Buffer.from("\n" + line(1) + "\n" + line(2) + "\n")]));

    const tail = createSpeechTail(logPath);
    const lines = tail.readNew();

    expect(tail.position()).toBe(fs.statSync(logPath).size);
    expect(lines.at(-1)).toBe(line(2));
  });

  it("同じ内容をもう一度読んでも再配信しない", () => {
    fs.writeFileSync(logPath, Buffer.concat([TORN, Buffer.from("\n" + line(1) + "\n")]));

    const tail = createSpeechTail(logPath);
    tail.readNew();

    // position が進みすぎていると size < position になり、世代交代と誤認して全部を再送する
    expect(tail.readNew()).toEqual([]);
  });

  it("続きが追記されたとき、行の先頭が欠けない", () => {
    fs.writeFileSync(logPath, Buffer.concat([TORN, Buffer.from("\n" + line(1) + "\n")]));

    const tail = createSpeechTail(logPath);
    tail.readNew();

    append(2);
    expect(tail.readNew()).toEqual([line(2)]);
  });
});
