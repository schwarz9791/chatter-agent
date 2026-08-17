import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createSummaryPipeline, type SummaryPipelineDeps } from "./summaryPipeline";

let dir: string;
let homeDir: string;
let logPath: string;

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-summarizer-pipeline-"));
  homeDir = path.join(dir, "home");
  logPath = path.join(dir, "summarizer.log");
});

afterEach(() => {
  fs.rmSync(dir, { recursive: true, force: true });
  delete process.env.MARKER_PATH;
  delete process.env.MARKER_RESULT_PATH;
});

/**
 * フェイク要約 CLI。shebang + 実行権限で「本物の claude コマンド」と同じ形で直接 exec できるように
 * している（summaryPipeline は commandPath をそのまま execFileSync に渡す。node 経由でラップしない）。
 *
 * 環境変数:
 *   RECORDER_MODE        short（既定・固定の短文を返す）/ cat（stdin をそのまま返す）/
 *                         fail（stderr を書いて exit 1）/ hang（返ってこない）
 *   RECORDER_REPLY       short モードで返す文字列
 *   RECORD_LOG           受け取った argv 等を1行1JSONで追記するファイル（省略可）
 *   MARKER_PATH / MARKER_RESULT_PATH  registerSessionId の順序検証用（下のテスト参照）
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
  const sessionIdIdx = argv.indexOf("--session-id");
  const sessionId = sessionIdIdx >= 0 ? argv[sessionIdIdx + 1] : null;
  const modelIdx = argv.indexOf("--model");
  const model = modelIdx >= 0 ? argv[modelIdx + 1] : null;
  fs.appendFileSync(recordLog, JSON.stringify({ argv, sessionId, model, mode }) + "\\n");
}

const markerPath = process.env.MARKER_PATH;
const markerResultPath = process.env.MARKER_RESULT_PATH;
if (markerPath && markerResultPath) {
  fs.appendFileSync(markerResultPath, fs.existsSync(markerPath) ? "seen\\n" : "missing\\n");
}

if (mode === "fail") {
  process.stderr.write("summarizer cli failed intentionally");
  process.exit(1);
} else if (mode === "hang") {
  setInterval(() => {}, 1000);
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

function makeDeps(overrides: Partial<SummaryPipelineDeps> = {}): SummaryPipelineDeps {
  return {
    isEnabled: () => true,
    getThreshold: () => 10,
    getTimeoutMs: () => 5000,
    getMaxPerDrain: () => 10,
    getCommand: () => writeRecorderScript(),
    getModel: () => "",
    homeDir,
    logPath,
    ...overrides,
  };
}

const LONG_TEXT = "これはとても長い要約対象のテキストです。閾値を超えるように十分な長さにしてあります。";

function readLogLines(): string[][] {
  if (!fs.existsSync(logPath)) return [];
  return fs
    .readFileSync(logPath, "utf-8")
    .split("\n")
    .filter((l) => l.length > 0)
    .map((l) => l.split("\t"));
}

describe("createSummaryPipeline", () => {
  it("無効なら原文を返し、CLI を一度も起動しない（ログも書かない）", () => {
    const recordLog = path.join(dir, "record.jsonl");
    process.env.RECORD_LOG = recordLog;
    const script = writeRecorderScript();
    const summarize = createSummaryPipeline(makeDeps({ isEnabled: () => false, getCommand: () => script }));

    expect(summarize(LONG_TEXT, () => {})).toBe(LONG_TEXT);
    expect(fs.existsSync(logPath)).toBe(false);
    expect(fs.existsSync(recordLog)).toBe(false); // recorder.mjs 自身が一度も走っていない証拠

    delete process.env.RECORD_LOG;
  });

  it("閾値以下なら原文を返し、CLI を一度も起動しない（ログも書かない）", () => {
    const recordLog = path.join(dir, "record.jsonl");
    process.env.RECORD_LOG = recordLog;
    const script = writeRecorderScript();
    const summarize = createSummaryPipeline(makeDeps({ getThreshold: () => 1000, getCommand: () => script }));

    expect(summarize(LONG_TEXT, () => {})).toBe(LONG_TEXT);
    expect(fs.existsSync(logPath)).toBe(false);
    expect(fs.existsSync(recordLog)).toBe(false);

    delete process.env.RECORD_LOG;
  });

  it("上限に達したら以降は原文を返し、CLI は上限の回数しか起動しない", () => {
    const recordLog = path.join(dir, "record.jsonl");
    process.env.RECORDER_MODE = "short";
    const summarize = createSummaryPipeline(
      makeDeps({
        getMaxPerDrain: () => 1,
        getCommand: () => {
          process.env.RECORD_LOG = recordLog;
          return writeRecorderScript();
        },
      }),
    );

    const first = summarize(LONG_TEXT, () => {});
    const second = summarize(LONG_TEXT + "2", () => {});

    expect(first).not.toBe(LONG_TEXT); // 1件目は要約が採用される
    expect(second).toBe(LONG_TEXT + "2"); // 2件目は上限超過で原文

    const invocations = fs
      .readFileSync(recordLog, "utf-8")
      .split("\n")
      .filter((l) => l.length > 0);
    expect(invocations).toHaveLength(1);

    const outcomes = readLogLines().map((l) => l[1]);
    expect(outcomes).toEqual(["ok", "skipped-limit"]);

    delete process.env.RECORDER_MODE;
    delete process.env.RECORD_LOG;
  });

  it("空 or 原文以上の長さの要約は不採用（stdin をそのまま返す cat モードで確認する）", () => {
    // ★ 本物の /bin/cat は使わない。buildSummaryArgs が積む `-p` を BSD/GNU どちらの cat も
    //   未知のオプションとして拒否し（`cat: illegal option -- p`）、狙った「stdin をそのまま
    //   返す」ではなく非ゼロ終了になってしまうと実測で確認した。recorder.mjs の cat モードは
    //   argv を無視して stdin をそのまま返すので、同じ意図をポータブルに再現できる
    process.env.RECORDER_MODE = "cat";
    const summarize = createSummaryPipeline(makeDeps());

    expect(summarize(LONG_TEXT, () => {})).toBe(LONG_TEXT);
    const lines = readLogLines();
    expect(lines).toHaveLength(1);
    expect(lines[0]![1]).toBe("invalid");

    delete process.env.RECORDER_MODE;
  });

  it("非ゼロ終了は例外を投げずに原文へフォールバックする", () => {
    process.env.RECORDER_MODE = "fail";
    const summarize = createSummaryPipeline(makeDeps());

    let result: string | undefined;
    expect(() => {
      result = summarize(LONG_TEXT, () => {});
    }).not.toThrow();
    expect(result).toBe(LONG_TEXT);

    const lines = readLogLines();
    expect(lines[0]![1]).toBe("error");
    delete process.env.RECORDER_MODE;
  });

  it("タイムアウトは例外を投げずに原文へフォールバックする（タイムアウト値は短くしてある）", () => {
    process.env.RECORDER_MODE = "hang";
    const summarize = createSummaryPipeline(makeDeps({ getTimeoutMs: () => 200 }));

    let result: string | undefined;
    expect(() => {
      result = summarize(LONG_TEXT, () => {});
    }).not.toThrow();
    expect(result).toBe(LONG_TEXT);

    const lines = readLogLines();
    expect(lines[0]![1]).toBe("timeout");
    delete process.env.RECORDER_MODE;
  }, 10_000);

  it("コマンドが見つからないときは例外を投げずに原文へフォールバックする", () => {
    const summarize = createSummaryPipeline(makeDeps({ getCommand: () => "definitely-not-a-real-command-xyz-12345" }));

    let result: string | undefined;
    expect(() => {
      result = summarize(LONG_TEXT, () => {});
    }).not.toThrow();
    expect(result).toBe(LONG_TEXT);

    const lines = readLogLines();
    expect(lines[0]![1]).toBe("no-command");
  });

  it("成功時は cleanTextForSpeech で再クリーニングした要約を返す", () => {
    process.env.RECORDER_MODE = "short";
    process.env.RECORDER_REPLY = "## 見出し\n要約です。`code` を直しました。";
    const summarize = createSummaryPipeline(makeDeps());

    const result = summarize(LONG_TEXT, () => {});
    expect(result).not.toContain("##");
    expect(result).not.toContain("`");
    expect(result).toContain("要約です。");

    const lines = readLogLines();
    expect(lines[0]![1]).toBe("ok");

    delete process.env.RECORDER_MODE;
    delete process.env.RECORDER_REPLY;
  });

  it("--session-id は呼び出しごとに変わる", () => {
    process.env.RECORDER_MODE = "short";
    const recordLog = path.join(dir, "record.jsonl");
    process.env.RECORD_LOG = recordLog;
    const summarize = createSummaryPipeline(makeDeps({ getMaxPerDrain: () => 10 }));

    summarize(LONG_TEXT, () => {});
    summarize(LONG_TEXT + "2", () => {});

    const records = fs
      .readFileSync(recordLog, "utf-8")
      .split("\n")
      .filter((l) => l.length > 0)
      .map((l) => JSON.parse(l) as { sessionId: string | null });

    expect(records).toHaveLength(2);
    expect(records[0]!.sessionId).toBeTruthy();
    expect(records[1]!.sessionId).toBeTruthy();
    expect(records[0]!.sessionId).not.toBe(records[1]!.sessionId);

    delete process.env.RECORDER_MODE;
    delete process.env.RECORD_LOG;
  });

  it("model が空なら --model を渡さず、指定すれば --model <model> が渡る", () => {
    process.env.RECORDER_MODE = "short";
    const recordLog = path.join(dir, "record.jsonl");
    process.env.RECORD_LOG = recordLog;

    let model = "";
    const summarize = createSummaryPipeline(makeDeps({ getModel: () => model, getMaxPerDrain: () => 10 }));

    summarize(LONG_TEXT, () => {});
    model = "haiku";
    summarize(LONG_TEXT + "2", () => {});

    const records = fs
      .readFileSync(recordLog, "utf-8")
      .split("\n")
      .filter((l) => l.length > 0)
      .map((l) => JSON.parse(l) as { model: string | null });

    expect(records[0]!.model).toBeNull();
    expect(records[1]!.model).toBe("haiku");

    delete process.env.RECORDER_MODE;
    delete process.env.RECORD_LOG;
  });

  it("registerSessionId は CLI 実行開始より前に呼ばれる（フェイクCLI側から観測する）", () => {
    process.env.RECORDER_MODE = "short";
    process.env.MARKER_PATH = path.join(dir, "marker");
    process.env.MARKER_RESULT_PATH = path.join(dir, "marker-result");
    const summarize = createSummaryPipeline(makeDeps());

    summarize(LONG_TEXT, (sessionId) => {
      expect(sessionId).toBeTruthy();
      // ★ ここでの副作用（マーカーの作成）が、CLI プロセスの起動より前に完了していることを、
      //   CLI 自身（recorder.mjs）に観測させる。JS は同期的なので registerSessionId の呼び出し自体は
      //   execFileSync より必ず先に評価されるが、契約として重要なのは「後回しにしない」実装であること
      fs.writeFileSync(process.env.MARKER_PATH!, "registered");
    });

    const result = fs.readFileSync(process.env.MARKER_RESULT_PATH!, "utf-8").trim();
    expect(result).toBe("seen");

    delete process.env.RECORDER_MODE;
  });

  it("ログ書き込みが失敗しても要約の結果自体は返る（発話を止めない）", () => {
    process.env.RECORDER_MODE = "short";
    const brokenLogPath = path.join(dir, "no-such-dir", "nested", "summarizer.log");
    const summarize = createSummaryPipeline(makeDeps({ logPath: brokenLogPath }));

    let result: string | undefined;
    expect(() => {
      result = summarize(LONG_TEXT, () => {});
    }).not.toThrow();
    expect(result).toBe("短い要約です。");

    delete process.env.RECORDER_MODE;
  });

  it("実測ログは1行ずつ追記され、ISO時刻・所要ms・原文長・要約長のタブ区切りになる", () => {
    process.env.RECORDER_MODE = "short";
    const summarize = createSummaryPipeline(makeDeps());

    summarize(LONG_TEXT, () => {});

    const lines = readLogLines();
    expect(lines).toHaveLength(1);
    const [ts, outcome, elapsedMs, textLength, summaryLength] = lines[0]!;
    expect(new Date(ts!).toISOString()).toBe(ts);
    expect(outcome).toBe("ok");
    expect(Number(elapsedMs)).toBeGreaterThanOrEqual(0);
    expect(Number(textLength)).toBe(LONG_TEXT.length);
    expect(Number(summaryLength)).toBe("短い要約です。".length);

    delete process.env.RECORDER_MODE;
  });

  // ★ 「throw しない」は型（types.ts の Summarize）で宣言した契約で、個々の経路が例外を
  //   投げないように書いてあるだけでは足りない。ここが漏れると worker.ts の processMessage が
  //   publish の手前で抜け、spool も消えず tombstone も付かないまま、そのメッセージが
  //   二度と発話されない（次のドレインでも同じ例外を踏み続け、孤児掃除まで沈黙する）。
  //   注入された getter が壊れているケースを代表として、最後の砦が効いていることを固定する
  it("★ 注入された getter が throw しても、例外を漏らさず原文を返す（契約の最後の砦）", () => {
    const boom = () => {
      throw new Error("設定の読み取りに失敗しました");
    };

    for (const overrides of [
      { isEnabled: boom as () => boolean },
      { getThreshold: boom as () => number },
      { getCommand: boom as () => string },
      { getModel: boom as () => string },
    ]) {
      const summarize = createSummaryPipeline(makeDeps(overrides));
      let result: string | undefined;
      expect(() => {
        result = summarize(LONG_TEXT, () => {});
      }).not.toThrow();
      expect(result).toBe(LONG_TEXT);
    }
  });

  it("★ registerSessionId が throw しても、例外を漏らさず原文を返す", () => {
    process.env.RECORDER_MODE = "short";
    const summarize = createSummaryPipeline(makeDeps());

    let result: string | undefined;
    expect(() => {
      result = summarize(LONG_TEXT, () => {
        // 呼び出し側（worker.ts）は state の永続化をここでする。ディスクが埋まった等で
        // 失敗しても、発話は原文で続けるのが正しい
        throw new Error("speak.state.json を書けませんでした");
      });
    }).not.toThrow();
    expect(result).toBe(LONG_TEXT);

    delete process.env.RECORDER_MODE;
  });
});
