import * as path from "path";
import { describe, it, expect } from "vitest";
import {
  getConfigFilePath,
  getLockDir,
  getPlayerLockDir,
  getPlayerTmpDir,
  getRuntimeDir,
  getServerLockDir,
  getSpeechLogBackupPath,
  getSpeechLogPath,
  getSpeechQueueDir,
  getSpeechStatePath,
  getSpoolDir,
  getWorkerStatePath,
  type PathEnv,
} from "./paths";

function env(platform: NodeJS.Platform, overrides: NodeJS.ProcessEnv = {}): PathEnv {
  return { platform, homedir: "/home/user", env: overrides };
}

describe("getRuntimeDir", () => {
  it("darwin では XDG 既定の ~/.config を使う", () => {
    expect(getRuntimeDir(env("darwin"))).toBe("/home/user/.config/chatter-agent");
  });

  it("linux でも同じ", () => {
    expect(getRuntimeDir(env("linux"))).toBe("/home/user/.config/chatter-agent");
  });

  it("XDG_CONFIG_HOME が設定されていればそれを使う", () => {
    expect(getRuntimeDir(env("darwin", { XDG_CONFIG_HOME: "/custom/cfg" }))).toBe("/custom/cfg/chatter-agent");
  });

  it("win32 では APPDATA を使う", () => {
    expect(getRuntimeDir(env("win32", { APPDATA: "/appdata" }))).toBe("/appdata/chatter-agent");
  });

  it("win32 で APPDATA が無ければ AppData/Roaming にフォールバックする", () => {
    expect(getRuntimeDir(env("win32"))).toBe("/home/user/AppData/Roaming/chatter-agent");
  });
});

describe("ランタイムルート配下のパス", () => {
  const e = env("darwin");
  const root = "/home/user/.config/chatter-agent";

  it("すべてランタイムルートの直下に置く（plugin の bash が辿れる場所を1箇所に絞るため）", () => {
    expect(getConfigFilePath(e)).toBe(`${root}/config.json`);
    expect(getSpoolDir(e)).toBe(`${root}/spool`);
    expect(getSpeechLogPath(e)).toBe(`${root}/speech.jsonl`);
    expect(getSpeechQueueDir(e)).toBe(`${root}/speech`);
    expect(getSpeechStatePath(e)).toBe(`${root}/speech.state.json`);
    expect(getWorkerStatePath(e)).toBe(`${root}/speak.state.json`);
    expect(getLockDir(e)).toBe(`${root}/speak.lock`);
    expect(getServerLockDir(e)).toBe(`${root}/server.lock`);
    expect(getPlayerLockDir(e)).toBe(`${root}/player.lock`);
    expect(getPlayerTmpDir(e)).toBe(`${root}/player-tmp`);
  });

  it("記録（speech.jsonl）と配信キュー（speech/）は別物", () => {
    expect(getSpeechLogPath(e)).not.toBe(getSpeechQueueDir(e));
  });

  it("CLI のロック（speak.lock）とサーバーのロック（server.lock）は別物", () => {
    // 同居すると、サーバーの単一インスタンス化が CLI のロックを巻き添えにしてしまう
    expect(getLockDir(e)).not.toBe(getServerLockDir(e));
  });

  it("3つのロックはすべて別物", () => {
    // player は server とも CLI とも独立に動く。共有すると、どれか1つの起動が他を締め出す
    expect(new Set([getLockDir(e), getServerLockDir(e), getPlayerLockDir(e)]).size).toBe(3);
  });

  it("player の一時ディレクトリは配信キューと混ざらない", () => {
    // 起動時にディレクトリごと消すので、混ざっていると未配信のキューを巻き添えにする
    expect(getPlayerTmpDir(e)).not.toBe(getSpeechQueueDir(e));
  });

  it("CHATTER_AGENT_CONFIG は設定ファイルだけを上書きする", () => {
    const overridden = env("darwin", { CHATTER_AGENT_CONFIG: "/tmp/my.json" });
    expect(getConfigFilePath(overridden)).toBe("/tmp/my.json");
    // spool までは動かない（設定の置き場所とランタイムの置き場所は別）
    expect(getSpoolDir(overridden)).toBe(`${root}/spool`);
  });
});

describe("getSpeechLogBackupPath", () => {
  it("拡張子の手前に .1 を挿す", () => {
    expect(getSpeechLogBackupPath("/x/speech.jsonl")).toBe("/x/speech.1.jsonl");
  });

  it("拡張子が無くても壊れない", () => {
    expect(getSpeechLogBackupPath("/x/speech")).toBe("/x/speech.1");
  });
});

describe("既定引数", () => {
  it("引数なしでも実環境から解決できる", () => {
    expect(getSpoolDir().endsWith(path.join("chatter-agent", "spool"))).toBe(true);
  });
});
