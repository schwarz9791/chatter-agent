/**
 * `spawn` をモックせず、実際にコマンドを起動して検証する（`wsServer.test.ts` と同じ方針）。
 * `/usr/bin/true` / `/usr/bin/false` / `/bin/sleep` は macOS と Linux の両方にある。
 */

import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createAudioPlayer, playbackTimeoutMs, wavDurationMs } from "./audioPlayer";

let dir: string;
let tmpDir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-player-"));
  tmpDir = path.join(dir, "player-tmp");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

/** 指定秒ぶんの、最小限だが正しい RIFF/PCM を作る */
function wav(seconds: number, options: { dataSize?: number; extraChunk?: boolean } = {}): ArrayBuffer {
  const sampleRate = 24000;
  const byteRate = sampleRate * 2; // 16bit mono
  const dataBytes = Math.round(byteRate * seconds);
  const extra = options.extraChunk ? 12 : 0;
  const buffer = Buffer.alloc(44 + extra + dataBytes);

  buffer.write("RIFF", 0);
  buffer.writeUInt32LE(36 + extra + dataBytes, 4);
  buffer.write("WAVE", 8);

  buffer.write("fmt ", 12);
  buffer.writeUInt32LE(16, 16);
  buffer.writeUInt16LE(1, 20); // PCM
  buffer.writeUInt16LE(1, 22); // mono
  buffer.writeUInt32LE(sampleRate, 24);
  buffer.writeUInt32LE(byteRate, 28);
  buffer.writeUInt16LE(2, 32);
  buffer.writeUInt16LE(16, 34);

  let offset = 36;
  if (options.extraChunk) {
    // fmt と data の間に別のチャンクが挟まるケース（LIST など）
    buffer.write("LIST", offset);
    buffer.writeUInt32LE(4, offset + 4);
    buffer.write("INFO", offset + 8);
    offset += 12;
  }

  buffer.write("data", offset);
  buffer.writeUInt32LE(options.dataSize ?? dataBytes, offset + 4);
  return buffer.buffer.slice(buffer.byteOffset, buffer.byteOffset + buffer.byteLength) as ArrayBuffer;
}

function player(command: string, args: string[]) {
  const p = createAudioPlayer({ tmpDir, command, args });
  p.reset();
  return p;
}

describe("wavDurationMs", () => {
  it("RIFF/PCM から秒数を読む", () => {
    expect(wavDurationMs(wav(0.5))).toBe(500);
    expect(wavDurationMs(wav(3))).toBe(3000);
  });

  it("fmt と data の間に別チャンクが挟まっても読める", () => {
    expect(wavDurationMs(wav(1, { extraChunk: true }))).toBe(1000);
  });

  it("data のサイズ宣言が嘘（ストリーミング書き出し）なら実体で測る", () => {
    expect(wavDurationMs(wav(2, { dataSize: 0 }))).toBe(2000);
    expect(wavDurationMs(wav(2, { dataSize: 0xffffffff }))).toBe(2000);
  });

  it("WAV でなければ null", () => {
    expect(wavDurationMs(new ArrayBuffer(0))).toBeNull();
    expect(wavDurationMs(Buffer.from("not a wav at all").buffer as ArrayBuffer)).toBeNull();
  });
});

describe("playbackTimeoutMs", () => {
  it("実長の2倍 + 余裕", () => {
    expect(playbackTimeoutMs(wav(3))).toBe(11_000);
  });

  it("★ 読めないときは固定の上限に倒す（固定値だけだと長文が切れるかハングを見逃す）", () => {
    expect(playbackTimeoutMs(new ArrayBuffer(0))).toBe(120_000);
  });
});

describe("write / discard", () => {
  it("seq をゼロ埋めしたファイル名で書く（配信キューと同じ規則）", () => {
    const p = player("/usr/bin/true", []);
    const file = p.write(42, wav(0.1));
    expect(path.basename(file)).toBe("000000000042.wav");
    expect(fs.existsSync(file)).toBe(true);
  });

  it("消したファイルを二度消しても落ちない", () => {
    const p = player("/usr/bin/true", []);
    const file = p.write(1, wav(0.1));
    p.discard(file);
    p.discard(file);
    expect(fs.existsSync(file)).toBe(false);
  });

  it("reset は前回の残骸を消す", () => {
    const p = player("/usr/bin/true", []);
    p.write(1, wav(0.1));
    p.reset();
    expect(fs.readdirSync(tmpDir)).toEqual([]);
  });
});

describe("play", () => {
  it("正常終了で解決する", async () => {
    const p = player("/usr/bin/true", ["{file}"]);
    const file = p.write(1, wav(0.1));
    await expect(p.play(file, 5000)).resolves.toBeUndefined();
  });

  it("異常終了は理由つきで reject する", async () => {
    const p = player("/usr/bin/false", ["{file}"]);
    const file = p.write(1, wav(0.1));
    await expect(p.play(file, 5000)).rejects.toThrow("異常終了");
  });

  it("★ コマンドが無ければ error イベントで決着する（exit だけ待つと永久に止まる）", async () => {
    const p = player("/nonexistent/player", ["{file}"]);
    const file = p.write(1, wav(0.1));
    await expect(p.play(file, 5000)).rejects.toThrow("起動できません");
  });

  it("★ ハングしたらタイムアウトして次へ進める", async () => {
    // Bluetooth が切れた afplay は戻ってこないことがある。head-of-line blocking なので
    // 1回のハングで以後すべてが無音になる
    const p = player("/bin/sleep", ["30"]);
    const file = p.write(1, wav(0.1));
    await expect(p.play(file, 200)).rejects.toThrow("終わりませんでした");
  });

  it("{file} が実際のパスに置換される", async () => {
    // cat は引数のファイルを読むので、置換に失敗していれば
    // "{file}" という存在しないパスとして異常終了する
    const p = player("/bin/cat", ["{file}"]);
    const file = p.write(7, wav(0.1));
    await expect(p.play(file, 5000)).resolves.toBeUndefined();
  });

  it("stopAll は再生中のプロセスを止める", async () => {
    const p = player("/bin/sleep", ["30"]);
    const file = p.write(1, wav(0.1));
    const playing = p.play(file, 30_000);
    // 起動を待ってから止める
    await new Promise((r) => setTimeout(r, 50));
    p.stopAll();
    await expect(playing).rejects.toThrow("異常終了");
  });
});

describe("cleanup", () => {
  it("一時ディレクトリごと消す", () => {
    const p = player("/usr/bin/true", []);
    p.write(1, wav(0.1));
    p.cleanup();
    expect(fs.existsSync(tmpDir)).toBe(false);
  });
});
