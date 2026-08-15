import * as path from "path";
import { describe, it, expect } from "vitest";
import {
  getConfigFilePath,
  getLockDir,
  getRuntimeDir,
  getSpeechLogGenerationPath,
  getSpeechLogPath,
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
    expect(getSpeechStatePath(e)).toBe(`${root}/speech.state.json`);
    expect(getWorkerStatePath(e)).toBe(`${root}/speak.state.json`);
    expect(getLockDir(e)).toBe(`${root}/speak.lock`);
  });

  it("CHATTER_AGENT_CONFIG は設定ファイルだけを上書きする", () => {
    const overridden = env("darwin", { CHATTER_AGENT_CONFIG: "/tmp/my.json" });
    expect(getConfigFilePath(overridden)).toBe("/tmp/my.json");
    // spool までは動かない（設定の置き場所とランタイムの置き場所は別）
    expect(getSpoolDir(overridden)).toBe(`${root}/spool`);
  });
});

describe("getSpeechLogGenerationPath", () => {
  it("世代番号を拡張子の手前に挿す", () => {
    expect(getSpeechLogGenerationPath("/x/speech.jsonl", 1)).toBe("/x/speech.1.jsonl");
    expect(getSpeechLogGenerationPath("/x/speech.jsonl", 3)).toBe("/x/speech.3.jsonl");
  });

  it("0 以下は現世代そのもの", () => {
    expect(getSpeechLogGenerationPath("/x/speech.jsonl", 0)).toBe("/x/speech.jsonl");
    expect(getSpeechLogGenerationPath("/x/speech.jsonl", -1)).toBe("/x/speech.jsonl");
  });

  it("拡張子が無くても壊れない", () => {
    expect(getSpeechLogGenerationPath("/x/speech", 2)).toBe("/x/speech.2");
  });
});

describe("既定引数", () => {
  it("引数なしでも実環境から解決できる", () => {
    expect(getSpoolDir().endsWith(path.join("chatter-agent", "spool"))).toBe(true);
  });
});
