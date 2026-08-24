/**
 * `findCommandPath` のテスト。**`summarizer/claudeCli.test.ts` から移した**（#51 で
 * 関数が `core/commandPath.ts` へ出たため）。内容は変えていない。
 */

import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { findCommandPath } from "./commandPath";

let dir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-command-path-"));
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

describe("findCommandPath", () => {
  it("絶対パスならそのまま返す（存在確認はしない）", () => {
    const missing = path.join(dir, "no-such-binary");
    expect(findCommandPath(missing)).toBe(missing);
  });

  it("PATH 上のディレクトリから見つける", () => {
    const binDir = path.join(dir, "path-bin");
    fs.mkdirSync(binDir);
    const bin = path.join(binDir, "claude");
    fs.writeFileSync(bin, "#!/bin/sh\n");
    fs.chmodSync(bin, 0o755);

    const found = findCommandPath("claude", { env: { PATH: binDir }, homeDir: path.join(dir, "empty-home") });
    expect(found).toBe(bin);
  });

  it("PATH に無くても既知の bin ディレクトリ（~/.local/bin）から見つける", () => {
    const homeDir = path.join(dir, "home");
    const knownDir = path.join(homeDir, ".local", "bin");
    fs.mkdirSync(knownDir, { recursive: true });
    const bin = path.join(knownDir, "claude");
    fs.writeFileSync(bin, "#!/bin/sh\n");
    fs.chmodSync(bin, 0o755);

    const found = findCommandPath("claude", { env: { PATH: "" }, homeDir });
    expect(found).toBe(bin);
  });

  it("nvm のバージョン別 bin ディレクトリも探す", () => {
    const homeDir = path.join(dir, "home");
    const nvmBin = path.join(homeDir, ".nvm", "versions", "node", "v24.19.0", "bin");
    fs.mkdirSync(nvmBin, { recursive: true });
    const bin = path.join(nvmBin, "claude");
    fs.writeFileSync(bin, "#!/bin/sh\n");
    fs.chmodSync(bin, 0o755);

    const found = findCommandPath("claude", { env: { PATH: "" }, homeDir });
    expect(found).toBe(bin);
  });

  it("★ mise の shim ディレクトリ（~/.local/share/mise/shims）も探す（対話 rc を経由しない起動で PATH に載らない既定事実への対応）", () => {
    const homeDir = path.join(dir, "home");
    const miseShims = path.join(homeDir, ".local", "share", "mise", "shims");
    fs.mkdirSync(miseShims, { recursive: true });
    const bin = path.join(miseShims, "claude");
    fs.writeFileSync(bin, "#!/bin/sh\n");
    fs.chmodSync(bin, 0o755);

    const found = findCommandPath("claude", { env: { PATH: "" }, homeDir });
    expect(found).toBe(bin);
  });

  it("asdf の shim ディレクトリ（~/.asdf/shims）も探す", () => {
    const homeDir = path.join(dir, "home");
    const asdfShims = path.join(homeDir, ".asdf", "shims");
    fs.mkdirSync(asdfShims, { recursive: true });
    const bin = path.join(asdfShims, "claude");
    fs.writeFileSync(bin, "#!/bin/sh\n");
    fs.chmodSync(bin, 0o755);

    const found = findCommandPath("claude", { env: { PATH: "" }, homeDir });
    expect(found).toBe(bin);
  });

  // ★ fnm のテストは置かない。fnm は shim を持たず、シェルごとの一時ディレクトリを PATH に
  //   挿す方式で、プロセスの外から当てられる固定の場所が無い（→ claudeCli.ts の
  //   getKnownBinDirs の註記）。テストを書くと「実装が探す場所」を追認するだけになり、
  //   実環境で見つかることを何も担保しない

  it("どこにも無ければ undefined", () => {
    const found = findCommandPath("definitely-not-a-real-command-xyz", {
      env: { PATH: path.join(dir, "empty") },
      homeDir: path.join(dir, "empty-home"),
    });
    expect(found).toBeUndefined();
  });

  it("ディレクトリと同名のファイルは候補にしない", () => {
    const binDir = path.join(dir, "path-bin2");
    fs.mkdirSync(binDir);
    // "claude" という名前の**ディレクトリ**を置く（isFile() が false になるはず）
    fs.mkdirSync(path.join(binDir, "claude"));

    const found = findCommandPath("claude", { env: { PATH: binDir }, homeDir: path.join(dir, "empty-home") });
    expect(found).toBeUndefined();
  });

  it("★ 実行ビットの無い同名ファイルは候補にせず、次の候補ディレクトリを探索する", () => {
    // ~/.local/bin にインストールの残骸（0644 の claude）があり、その後ろの PATH エントリに
    // 本物の実行可能ファイルがある状況を再現する。実行ビットを見ずに最初の一致で打ち切ると、
    // 残骸が本物を恒久的に隠してしまう
    const noExecDir = path.join(dir, "no-exec-bin");
    fs.mkdirSync(noExecDir);
    const noExecFile = path.join(noExecDir, "claude");
    fs.writeFileSync(noExecFile, "#!/bin/sh\n");
    fs.chmodSync(noExecFile, 0o644);

    const realDir = path.join(dir, "real-bin");
    fs.mkdirSync(realDir);
    const realFile = path.join(realDir, "claude");
    fs.writeFileSync(realFile, "#!/bin/sh\n");
    fs.chmodSync(realFile, 0o755);

    const found = findCommandPath("claude", {
      env: { PATH: [noExecDir, realDir].join(path.delimiter) },
      homeDir: path.join(dir, "empty-home"),
    });
    expect(found).toBe(realFile);
  });

  it("~/ で始まるパスは homeDir に展開される", () => {
    const homeDir = path.join(dir, "home-tilde");
    const binDir = path.join(homeDir, ".local", "bin");
    fs.mkdirSync(binDir, { recursive: true });
    const bin = path.join(binDir, "claude");
    fs.writeFileSync(bin, "#!/bin/sh\n");
    fs.chmodSync(bin, 0o755);

    const found = findCommandPath("~/.local/bin/claude", { env: { PATH: "" }, homeDir });
    expect(found).toBe(bin);
  });
});
