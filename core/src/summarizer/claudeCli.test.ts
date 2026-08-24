import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { buildSummaryArgs, buildSummaryEnv, runClaudeCli } from "./claudeCli";

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

  it("★ --disallowedTools は読み取り系・SlashCommand を含む固定リストと完全一致する（列挙漏れと余計な追加の両方を検出する）", () => {
    const args = buildSummaryArgs("x", { sessionId: "s", model: "" });
    const idx = args.indexOf("--disallowedTools");
    expect(idx).toBeGreaterThanOrEqual(0);
    const value = args[idx + 1];
    expect(value).toBe(
      "Agent,Task,Bash,BashOutput,KillShell,Edit,Write,NotebookEdit,WebFetch,WebSearch,Read,Glob,Grep,SlashCommand",
    );
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

describe("buildSummaryEnv", () => {
  it("★ 親セッションの認証・IPC変数（denylist）を落とし、無関係な変数と CHATTER_AGENT_DISABLE=1 は残す", () => {
    const env = buildSummaryEnv({
      CLAUDECODE: "1",
      CLAUDE_CODE_SESSION_ID: "x",
      ANTHROPIC_API_KEY: "y",
      CLAUDE_CONFIG_DIR: "/z",
    });

    // denylist に載っているキーは落ちる
    expect(env.CLAUDECODE).toBeUndefined();
    expect(env.CLAUDE_CODE_SESSION_ID).toBeUndefined();

    // denylist に無いキーはそのまま残る（allowlist ではなく denylist を選んだ理由の裏付け）
    expect(env.ANTHROPIC_API_KEY).toBe("y");
    expect(env.CLAUDE_CONFIG_DIR).toBe("/z");

    // 無限ループ防止の第1層は常に付く
    expect(env.CHATTER_AGENT_DISABLE).toBe("1");
  });

  it("denylist の全キーを落とす", () => {
    const parent: NodeJS.ProcessEnv = {
      CLAUDE_CODE_SESSION_ID: "1",
      CLAUDE_CODE_MESSAGING_TOKEN: "1",
      CLAUDE_CODE_MESSAGING_SOCKET: "1",
      CLAUDECODE: "1",
      CLAUDE_CODE_ENTRYPOINT: "1",
      CLAUDE_CODE_BRIDGE_SESSION_ID: "1",
      CLAUDE_CODE_CHILD_SESSION: "1",
      CLAUDE_PID: "1",
      CLAUDE_EFFORT: "1",
      CLAUDE_PROJECT_DIR: "1",
      CLAUDE_PLUGIN_ROOT: "1",
      CLAUDE_CODE_SSE_PORT: "1",
    };
    const env = buildSummaryEnv(parent);
    for (const key of Object.keys(parent)) {
      expect(env[key]).toBeUndefined();
    }
  });

  it("★ 絶対に落としてはいけない認証系の変数は残る（プレフィックス一括除去にしていないことの回帰確認）", () => {
    const env = buildSummaryEnv({
      CLAUDE_CONFIG_DIR: "/config",
      CLAUDE_CODE_OAUTH_TOKEN: "token",
      CLAUDE_CODE_USE_BEDROCK: "1",
      CLAUDE_CODE_USE_VERTEX: "1",
      CLAUDE_CODE_API_KEY_HELPER_TTL_MS: "1000",
      CLAUDE_CODE_EXECPATH: "/usr/local/bin/claude",
      ANTHROPIC_API_KEY: "secret",
      AWS_REGION: "us-east-1",
    });

    expect(env.CLAUDE_CONFIG_DIR).toBe("/config");
    expect(env.CLAUDE_CODE_OAUTH_TOKEN).toBe("token");
    expect(env.CLAUDE_CODE_USE_BEDROCK).toBe("1");
    expect(env.CLAUDE_CODE_USE_VERTEX).toBe("1");
    expect(env.CLAUDE_CODE_API_KEY_HELPER_TTL_MS).toBe("1000");
    expect(env.CLAUDE_CODE_EXECPATH).toBe("/usr/local/bin/claude");
    expect(env.ANTHROPIC_API_KEY).toBe("secret");
    expect(env.AWS_REGION).toBe("us-east-1");
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

  it("★ ハングするコマンドは timeoutMs で強制終了され reason: timeout になる（err.code === ETIMEDOUT で判定する）", () => {
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

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.reason).toBe("timeout");
  }, 10_000);

  it("★ maxBuffer 超過（ENOBUFS）は signal が付いていても timeout と誤報せず reason: overflow になる", () => {
    const script = writeScript(
      "overflow.mjs",
      `
      // MAX_BUFFER_BYTES（1MiB）を超える出力を stdout に吐く。
      // タイムアウトと同じく Node に SIGKILL で殺され signal が付くが、code は ENOBUFS になる
      // （ETIMEDOUT ではない）。signal の有無だけで判定すると timeout と誤報する
      process.stdout.write("x".repeat(2 * 1024 * 1024));
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
    expect(result.reason).toBe("overflow");
  });

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
