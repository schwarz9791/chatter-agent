import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { buildSummaryArgs, findCommandPath, runClaudeCli } from "./claudeCli";

let dir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-summarizer-claudecli-"));
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
});

/** 実行可能ビットを立てて node スクリプトを置く。process.execPath 経由で叩くので実行権限は必須ではないが、実CLIに近づけておく */
function writeScript(name: string, content: string): string {
  const file = path.join(dir, name);
  fs.writeFileSync(file, content);
  fs.chmodSync(file, 0o755);
  return file;
}

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

    const found = findCommandPath("claude", { env: { PATH: binDir }, homeDir: path.join(dir, "empty-home") });
    expect(found).toBe(bin);
  });

  it("PATH に無くても既知の bin ディレクトリ（~/.local/bin）から見つける", () => {
    const homeDir = path.join(dir, "home");
    const knownDir = path.join(homeDir, ".local", "bin");
    fs.mkdirSync(knownDir, { recursive: true });
    const bin = path.join(knownDir, "claude");
    fs.writeFileSync(bin, "#!/bin/sh\n");

    const found = findCommandPath("claude", { env: { PATH: "" }, homeDir });
    expect(found).toBe(bin);
  });

  it("nvm のバージョン別 bin ディレクトリも探す", () => {
    const homeDir = path.join(dir, "home");
    const nvmBin = path.join(homeDir, ".nvm", "versions", "node", "v24.19.0", "bin");
    fs.mkdirSync(nvmBin, { recursive: true });
    const bin = path.join(nvmBin, "claude");
    fs.writeFileSync(bin, "#!/bin/sh\n");

    const found = findCommandPath("claude", { env: { PATH: "" }, homeDir });
    expect(found).toBe(bin);
  });

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
});

describe("buildSummaryArgs", () => {
  it("-p に instruction、--session-id に渡した ID を積む", () => {
    const args = buildSummaryArgs("要約して", { sessionId: "abc-123", model: "" });
    expect(args).toContain("-p");
    expect(args[args.indexOf("-p") + 1]).toBe("要約して");
    expect(args).toContain("--session-id");
    expect(args[args.indexOf("--session-id") + 1]).toBe("abc-123");
  });

  it("--no-session-persistence と --strict-mcp-config を含む", () => {
    const args = buildSummaryArgs("x", { sessionId: "s", model: "" });
    expect(args).toContain("--no-session-persistence");
    expect(args).toContain("--strict-mcp-config");
  });

  it("--disallowedTools は現行名 Agent と旧名 Task を両方含む", () => {
    const args = buildSummaryArgs("x", { sessionId: "s", model: "" });
    const idx = args.indexOf("--disallowedTools");
    expect(idx).toBeGreaterThanOrEqual(0);
    const value = args[idx + 1];
    expect(value).toContain("Agent");
    expect(value).toContain("Task");
    expect(value).toContain("Bash");
    expect(value).toContain("Edit");
    expect(value).toContain("Write");
    expect(value).toContain("NotebookEdit");
    expect(value).toContain("WebFetch");
    expect(value).toContain("WebSearch");
  });

  it("model が空文字なら --model を含まない", () => {
    const args = buildSummaryArgs("x", { sessionId: "s", model: "" });
    expect(args).not.toContain("--model");
  });

  it("model を指定すると --model <model> が付く", () => {
    const args = buildSummaryArgs("x", { sessionId: "s", model: "haiku" });
    const idx = args.indexOf("--model");
    expect(idx).toBeGreaterThanOrEqual(0);
    expect(args[idx + 1]).toBe("haiku");
  });

  it("--setting-sources は渡さない（settings.json 由来の認証を壊すため採用しなかった）", () => {
    const args = buildSummaryArgs("x", { sessionId: "s", model: "" });
    expect(args).not.toContain("--setting-sources");
  });
});

describe("runClaudeCli", () => {
  it("stdin で渡した原文を受け取り、cwd と CHATTER_AGENT_DISABLE=1 を伝播する", () => {
    const script = writeScript(
      "echo.mjs",
      `
      import * as fs from "node:fs";
      const stdin = fs.readFileSync(0, "utf-8");
      process.stdout.write(JSON.stringify({
        stdin,
        cwd: process.cwd(),
        disable: process.env.CHATTER_AGENT_DISABLE ?? null,
      }));
      `,
    );
    const homeDir = path.join(dir, "home-not-yet-created");

    const result = runClaudeCli({
      commandPath: process.execPath,
      args: [script],
      text: "これは要約対象のテキストです",
      homeDir,
      timeoutMs: 5000,
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    const parsed = JSON.parse(result.stdout) as { stdin: string; cwd: string; disable: string | null };
    expect(parsed.stdin).toBe("これは要約対象のテキストです");
    expect(parsed.cwd).toBe(fs.realpathSync(homeDir));
    expect(parsed.disable).toBe("1");
  });

  it("homeDir が無ければ作る", () => {
    const homeDir = path.join(dir, "not-yet", "nested");
    expect(fs.existsSync(homeDir)).toBe(false);

    runClaudeCli({
      commandPath: process.execPath,
      args: ["-e", "process.exit(0)"],
      text: "",
      homeDir,
      timeoutMs: 5000,
    });

    expect(fs.existsSync(homeDir)).toBe(true);
  });

  it("stdout だけを要約文として trim して返す（stderr は無視する）", () => {
    const script = writeScript(
      "noisy.mjs",
      `
      process.stderr.write("Permission allow rule (...) is not matched by ...\\n");
      process.stdout.write("  短い要約です。  \\n");
      `,
    );

    const result = runClaudeCli({
      commandPath: process.execPath,
      args: [script],
      text: "x",
      homeDir: path.join(dir, "home"),
      timeoutMs: 5000,
    });

    expect(result).toEqual({ ok: true, stdout: "短い要約です。" });
  });

  it("非ゼロ終了は reason: error になり、stderr の内容を detail に含める", () => {
    const script = writeScript(
      "fail.mjs",
      `
      process.stderr.write("something went wrong");
      process.exit(1);
      `,
    );

    const result = runClaudeCli({
      commandPath: process.execPath,
      args: [script],
      text: "x",
      homeDir: path.join(dir, "home"),
      timeoutMs: 5000,
    });

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toBe("error");
    if (result.reason !== "error") return;
    expect(result.detail).toContain("something went wrong");
  });

  it("ハングするコマンドは timeoutMs で強制終了され reason: timeout になる", () => {
    const script = writeScript(
      "hang.mjs",
      `
      setInterval(() => {}, 1000);
      `,
    );

    const result = runClaudeCli({
      commandPath: process.execPath,
      args: [script],
      text: "x",
      homeDir: path.join(dir, "home"),
      timeoutMs: 200,
    });

    expect(result).toEqual({ ok: false, reason: "timeout" });
  }, 10_000);

  it("コマンドが存在しない（ENOENT）場合も throw せず reason: error を返す", () => {
    const result = runClaudeCli({
      commandPath: path.join(dir, "no-such-binary"),
      args: [],
      text: "x",
      homeDir: path.join(dir, "home"),
      timeoutMs: 5000,
    });

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toBe("error");
  });
});
