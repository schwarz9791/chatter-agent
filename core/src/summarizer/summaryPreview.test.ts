import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { runSummaryPreview, type SummaryPreviewDeps } from "./summaryPreview";

let dir: string;
let homeDir: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-summary-preview-"));
  homeDir = path.join(dir, "home");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
  delete process.env.RECORDER_MODE;
  delete process.env.RECORDER_REPLY;
  delete process.env.RECORD_LOG;
});

/**
 * フェイク要約 CLI。`summaryPipeline.test.ts` と同じ形（shebang + 実行権限）。
 * `runClaudeCliAsync` は `commandPath` をそのまま `execFile` に渡すので、node 経由でラップしない。
 */
function writeRecorderScript(): string {
  const file = path.join(dir, "recorder.mjs");
  fs.writeFileSync(
    file,
    `#!/usr/bin/env node
import * as fs from "node:fs";

const argv = process.argv.slice(2);
const mode = process.env.RECORDER_MODE ?? "short";
const recordLog = process.env.RECORD_LOG;

if (recordLog) {
  const i = argv.indexOf("--session-id");
  fs.appendFileSync(
    recordLog,
    JSON.stringify({ sessionId: i >= 0 ? argv[i + 1] : null, disable: process.env.CHATTER_AGENT_DISABLE ?? null }) + "\\n",
  );
}

if (mode === "fail") {
  process.stderr.write("summarizer cli failed intentionally");
  process.exit(1);
} else if (mode === "hang") {
  setInterval(() => {}, 1000);
} else if (mode === "flood") {
  process.stdout.write("x".repeat(4 * 1024 * 1024));
} else if (mode === "cat") {
  process.stdout.write(fs.readFileSync(0, "utf-8"));
} else {
  process.stdout.write(process.env.RECORDER_REPLY ?? "短い要約です。");
}
`,
  );
  fs.chmodSync(file, 0o755);
  return file;
}

const LONG_TEXT =
  "これはとても長いテスト用のテキストです。要約が原文より短くなることを確かめられる長さにしてあります。";

function makeDeps(overrides: Partial<SummaryPreviewDeps> = {}): SummaryPreviewDeps {
  return {
    getCommand: () => writeRecorderScript(),
    getModel: () => "",
    getTimeoutMs: () => 5000,
    homeDir,
    registerSessionId: () => {},
    ...overrides,
  };
}

describe("runSummaryPreview", () => {
  it("成功すると outcome=ok と要約を返す", async () => {
    process.env.RECORDER_REPLY = "短い要約です。";
    const result = await runSummaryPreview(LONG_TEXT, makeDeps());
    expect(result.outcome).toBe("ok");
    expect(result.summary).toBe("短い要約です。");
    expect(result.elapsedMs).toBeGreaterThanOrEqual(0);
  });

  /**
   * ★★ 無限ループ防止の第1層。`CHATTER_AGENT_DISABLE=1` が子に立っていないと、
   *   要約 CLI 自身の `MessageDisplay` hook が spool に積む
   */
  it("★★ 子プロセスに CHATTER_AGENT_DISABLE=1 が立っている", async () => {
    const recordLog = path.join(dir, "record.jsonl");
    process.env.RECORD_LOG = recordLog;
    await runSummaryPreview(LONG_TEXT, makeDeps());
    const line = JSON.parse(fs.readFileSync(recordLog, "utf-8").trim()) as { disable: string | null };
    expect(line.disable).toBe("1");
  });

  /** ★★ 無限ループ防止の第2層。CLI を起こす**前に**登録する契約 */
  it("★★ registerSessionId は CLI を起こす前に、渡した --session-id と同じ値で呼ばれる", async () => {
    const recordLog = path.join(dir, "record.jsonl");
    process.env.RECORD_LOG = recordLog;

    const registered: string[] = [];
    let registeredBeforeRun = false;
    await runSummaryPreview(
      LONG_TEXT,
      makeDeps({
        registerSessionId: (id) => {
          registered.push(id);
          registeredBeforeRun = !fs.existsSync(recordLog);
        },
      }),
    );

    expect(registeredBeforeRun).toBe(true);
    const line = JSON.parse(fs.readFileSync(recordLog, "utf-8").trim()) as { sessionId: string };
    expect(registered).toEqual([line.sessionId]);
  });

  /**
   * ★ 「登録できなければ CLI を起こさない」が第2層の安全側の挙動
   *   （`cli/worker.ts` の `registerSessionId` と同じ規律）
   */
  it("★ registerSessionId が throw したら CLI を起こさない", async () => {
    const recordLog = path.join(dir, "record.jsonl");
    process.env.RECORD_LOG = recordLog;

    const result = await runSummaryPreview(
      LONG_TEXT,
      makeDeps({
        registerSessionId: () => {
          throw new Error("ディスクフル");
        },
      }),
    );

    expect(result.outcome).toBe("internal");
    expect(result.summary).toBeNull();
    expect(fs.existsSync(recordLog)).toBe(false);
  });

  it("PATH に無いコマンドは no-command（CLI を起こさない）", async () => {
    const result = await runSummaryPreview(LONG_TEXT, makeDeps({ getCommand: () => "chatter-agent-no-such-command" }));
    expect(result.outcome).toBe("no-command");
    expect(result.summary).toBeNull();
  });

  /**
   * ★ 絶対パスは存在確認せずそのまま通る（`findCommandPath` の仕様）。
   *   その場合の失敗は `no-command` ではなく `error` になる
   */
  it("★ 存在しない絶対パスは error（no-command ではない）", async () => {
    const result = await runSummaryPreview(LONG_TEXT, makeDeps({ getCommand: () => "/no/such/command" }));
    expect(result.outcome).toBe("error");
  });

  it("CLI が非ゼロ終了したら error（stderr が detail に載る）", async () => {
    process.env.RECORDER_MODE = "fail";
    const result = await runSummaryPreview(LONG_TEXT, makeDeps());
    expect(result.outcome).toBe("error");
    expect(result.summary).toBeNull();
    expect(result.detail).toContain("failed intentionally");
  });

  /**
   * ★★ 非同期の `execFile` はタイムアウトで `ETIMEDOUT` を返さない（`killed: true` になる）。
   *   同期版の判定をコピーすると、タイムアウトが全部 `error` に化ける
   */
  it("★★ タイムアウトは timeout（error に化けない）", async () => {
    process.env.RECORDER_MODE = "hang";
    const result = await runSummaryPreview(LONG_TEXT, makeDeps({ getTimeoutMs: () => 200 }));
    expect(result.outcome).toBe("timeout");
    expect(result.summary).toBeNull();
  });

  /** ★ maxBuffer の判定を killed より先に置かないと overflow が timeout に化ける */
  it("★ maxBuffer 超過は overflow（timeout に化けない）", async () => {
    process.env.RECORDER_MODE = "flood";
    const result = await runSummaryPreview(LONG_TEXT, makeDeps());
    expect(result.outcome).toBe("overflow");
  });

  /** 採用の規則は本番（`isAcceptableSummary`）と同じものを見ている */
  it("原文より短くならなければ invalid", async () => {
    process.env.RECORDER_MODE = "cat";
    const result = await runSummaryPreview(LONG_TEXT, makeDeps());
    expect(result.outcome).toBe("invalid");
    expect(result.summary).toBeNull();
  });

  it("空の出力は invalid", async () => {
    process.env.RECORDER_REPLY = "";
    const result = await runSummaryPreview(LONG_TEXT, makeDeps());
    expect(result.outcome).toBe("invalid");
  });

  /** ★ 失敗のとき原文を返さない（テストの答えとして紛らわしい） */
  it("★ 失敗しても summary は null（原文を返さない）", async () => {
    process.env.RECORDER_MODE = "fail";
    const result = await runSummaryPreview(LONG_TEXT, makeDeps());
    expect(result.summary).toBeNull();
  });
});
