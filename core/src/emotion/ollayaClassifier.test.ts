import { describe, it, expect, afterEach } from "vitest";
import { spawn, type ChildProcess } from "child_process";
import { createOllayaEmotionClassifier } from "./ollayaClassifier";
import type { Emotion } from "../core/types";

function makeFallback(): { fn: (texts: string[]) => Emotion[]; calls: string[][] } {
  const calls: string[][] = [];
  const fn = (texts: string[]): Emotion[] => {
    calls.push(texts);
    return texts.map(() => "neutral");
  };
  return { fn, calls };
}

describe("createOllayaEmotionClassifier（spawnSync を差し替えた単体テスト）", () => {
  it("応答から最大値のラベルを文ごとに割り当てる", () => {
    const scores = [
      { first: { happy: 0.6, surprised: 0.1 }, final: { happy: 0.9, surprised: 0.05, neutral: 0.05 } },
      { first: { sad: 0.65, surprised: 0.8 }, final: { sad: 0.85, surprised: 0.05, neutral: 0.1 } },
    ];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["やりました！", "残念です。"])).toEqual(["happy", "sad"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("最上位の感情の確率が下限未満なら neutral にする", () => {
    const scores = [{ first: { happy: 0.6, surprised: 0.4 }, final: { happy: 0.5, surprised: 0.3, neutral: 0.2 } }];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["まあまあです。"])).toEqual(["neutral"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("happy 0.7（下限0.8未満）なら neutral にする", () => {
    const scores = [{ first: { happy: 0.6, surprised: 0.1 }, final: { happy: 0.7, surprised: 0.2, neutral: 0.1 } }];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["まあまあ嬉しいです。"])).toEqual(["neutral"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("relaxed 0.6（relaxed だけの下限0.5以上）なら relaxed にする", () => {
    const scores = [{ first: { relaxed: 0.3, surprised: 0.7 }, final: { relaxed: 0.6, happy: 0.3, neutral: 0.1 } }];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["結果を待ちます。"])).toEqual(["relaxed"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("relaxed 0.4（relaxed だけの下限0.5未満）なら neutral にする", () => {
    const scores = [{ first: { relaxed: 0.2, surprised: 0.8 }, final: { relaxed: 0.4, happy: 0.3, neutral: 0.3 } }];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["結果を待ちます。"])).toEqual(["neutral"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("neutral 自身の確率が最上位なら neutral にする", () => {
    const scores = [{ first: { happy: 0.6, surprised: 0.4 }, final: { happy: 0.2, surprised: 0.1, neutral: 0.7 } }];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["続きを進めます。"])).toEqual(["neutral"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("子プロセスが起動できない・非ゼロ終了なら全文を fallback する", () => {
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: "",
        stderr: "boom",
        status: 1,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["a", "b"])).toEqual(["neutral", "neutral"]);
    expect(fallback.calls).toEqual([["a", "b"]]);
  });

  it("壊れた JSON（配列でない）は全文を fallback する", () => {
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: "not json",
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["a"])).toEqual(["neutral"]);
  });

  it("一部の文だけ壊れた応答（null）なら、その文だけ fallback で埋める", () => {
    const scores = [
      { first: { happy: 0.6, surprised: 0.1 }, final: { happy: 0.9, surprised: 0.05, neutral: 0.05 } },
      null,
    ];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["a", "b"])).toEqual(["happy", "neutral"]);
    // fallback に渡るのは壊れていた1文だけ
    expect(fallback.calls).toEqual([["b"]]);
  });

  it("1段目で happy が過半未満（0.41）なら、2段目で0.93でも neutral にする", () => {
    const scores = [
      { first: { happy: 0.41, surprised: 0.99 }, final: { happy: 0.93, surprised: 0.05, neutral: 0.02 } },
    ];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["もう、またやってしまいました！"])).toEqual(["neutral"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("1段目で sad が過半以上（0.6）かつ2段目で0.9なら sad にする", () => {
    const scores = [{ first: { sad: 0.6, surprised: 0.1 }, final: { sad: 0.9, surprised: 0.05, neutral: 0.05 } }];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["すみません、修正し直します。"])).toEqual(["sad"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("relaxed / surprised には過半ルールが掛からない（1段目0.45でも2段目0.6なら relaxed にする）", () => {
    const scores = [{ first: { relaxed: 0.45, happy: 0.3 }, final: { relaxed: 0.6, happy: 0.3, neutral: 0.1 } }];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["結果を待ちます。"])).toEqual(["relaxed"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("first が欠けた応答なら、その文だけ fallback で埋める", () => {
    const scores = [
      { first: { happy: 0.6, surprised: 0.1 }, final: { happy: 0.9, surprised: 0.05, neutral: 0.05 } },
      { final: { sad: 0.9, surprised: 0.05, neutral: 0.05 } },
    ];
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify(scores),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["a", "b"])).toEqual(["happy", "neutral"]);
    // fallback に渡るのは first が欠けていた1文だけ
    expect(fallback.calls).toEqual([["b"]]);
  });

  it("応答配列の長さが texts と食い違えば全文を fallback する", () => {
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
      spawnSyncFn: (() => ({
        pid: 1,
        output: [],
        stdout: JSON.stringify([{ happy: 1, relaxed: 0, surprised: 0, sad: 0, angry: 0, neutral: 0 }]),
        stderr: "",
        status: 0,
        signal: null,
        error: undefined,
      })) as never,
    });

    expect(classify(["a", "b"])).toEqual(["neutral", "neutral"]);
    expect(fallback.calls).toEqual([["a", "b"]]);
  });

  it("空配列は子プロセスを起動せずに空配列を返す", () => {
    let called = false;
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:11435",
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: (texts) => texts.map(() => "neutral"),
      spawnSyncFn: (() => {
        called = true;
        throw new Error("呼ばれてはいけない");
      }) as never,
    });

    expect(classify([])).toEqual([]);
    expect(called).toBe(false);
  });
});

describe("createOllayaEmotionClassifier（実プロセス経由の結合テスト）", () => {
  // ★ `createOllayaEmotionClassifier` は `spawnSync`（同期）で子プロセスを起こす。
  //   スタブサーバーをこのテストプロセス自身の中で `http.createServer` すると、
  //   `spawnSync` がイベントループを止めている間はその接続を一切さばけず、
  //   子プロセスの fetch が `getTimeoutMs` まで固まって必ず失敗する（デッドロック）。
  //   本番では Ollaya は常に**別プロセス**（`ollaya serve`）なので起きない問題だが、
  //   テストのスタブも同じく別プロセスにして再現する。
  let stub: ChildProcess | undefined;

  afterEach(() => {
    stub?.kill();
    stub = undefined;
  });

  // ★ 実装の2段階（1回目 `questions.a` / `questions.b`、2回目 `questions.final`）を模す。
  //   どちらの回も `body.state` で判定するので、同じ文には一貫した勝者を返す。
  const STUB_SCRIPT = [
    'const http = require("http");',
    "const server = http.createServer((req, res) => {",
    "  const chunks = [];",
    '  req.on("data", (c) => chunks.push(c));',
    '  req.on("end", () => {',
    '    const body = JSON.parse(Buffer.concat(chunks).toString("utf-8"));',
    "    const q = body.questions || {};",
    '    const isHappy = body.state.includes("やりました");',
    "    if (q.a && q.b) {",
    '      if (q.a.type !== "choice" || q.b.type !== "choice") {',
    '        res.writeHead(400, { "Content-Type": "application/json" });',
    '        res.end(JSON.stringify({ error: "expected choice questions for a/b" }));',
    "        return;",
    "      }",
    "      const pa = { happy: isHappy ? 0.9 : 0.05, angry: 0.05, sad: isHappy ? 0.05 : 0.9 };",
    "      const pb = { relaxed: 0.1, surprised: 0.9 };",
    '      const winnerA = isHappy ? "happy" : "sad";',
    "      const answers = {",
    '        a: { type: "choice", choice: winnerA, confidence: pa[winnerA], probabilities: pa },',
    '        b: { type: "choice", choice: "surprised", confidence: pb.surprised, probabilities: pb },',
    "      };",
    '      res.writeHead(200, { "Content-Type": "application/json" });',
    "      res.end(JSON.stringify({ model: body.model, answers }));",
    "      return;",
    "    }",
    "    if (q.final) {",
    "      const criteriaKeys = Object.keys(q.final.criteria || {});",
    '      if (q.final.type !== "choice" || criteriaKeys.length !== 3 || criteriaKeys.indexOf("neutral") === -1) {',
    '        res.writeHead(400, { "Content-Type": "application/json" });',
    '        res.end(JSON.stringify({ error: "expected a 3-way final choice including neutral" }));',
    "        return;",
    "      }",
    '      const winner = isHappy ? "happy" : "sad";',
    "      const pf = {};",
    "      criteriaKeys.forEach((k) => { pf[k] = k === winner ? 0.9 : 0.05; });",
    "      const answers = {",
    '        final: { type: "choice", choice: winner, confidence: pf[winner], probabilities: pf },',
    "      };",
    '      res.writeHead(200, { "Content-Type": "application/json" });',
    "      res.end(JSON.stringify({ model: body.model, answers }));",
    "      return;",
    "    }",
    '    res.writeHead(400, { "Content-Type": "application/json" });',
    '    res.end(JSON.stringify({ error: "unexpected questions shape" }));',
    "  });",
    "});",
    'server.listen(0, "127.0.0.1", () => {',
    '  console.log("LISTENING:" + server.address().port);',
    "});",
  ].join("\n");

  /** Ollaya の `/v1/systemone` を模した最小のスタブを**別プロセス**で起こす */
  async function startStub(): Promise<string> {
    stub = spawn(process.execPath, ["-e", STUB_SCRIPT], { stdio: ["ignore", "pipe", "pipe"] });
    const port = await new Promise<number>((resolve, reject) => {
      let buf = "";
      stub!.stdout!.on("data", (chunk: Buffer) => {
        buf += chunk.toString("utf-8");
        const m = buf.match(/LISTENING:(\d+)/);
        if (m) resolve(Number(m[1]));
      });
      stub!.on("error", reject);
      setTimeout(() => reject(new Error("stub server did not start in time")), 5000).unref();
    });
    return `http://127.0.0.1:${port}`;
  }

  it("★ 実際に子プロセス（node -e）を起動し、fetch で /v1/systemone を叩いて argmax を返す", async () => {
    const baseUrl = await startStub();

    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => baseUrl,
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["やりました！", "残念です。"])).toEqual(["happy", "sad"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("接続できないポートでは fallback に落ちる（例外を投げない）", () => {
    const fallback = makeFallback();
    const classify = createOllayaEmotionClassifier({
      getBaseUrl: () => "http://127.0.0.1:1", // 特権ポート。まず開いていない
      getModel: () => "laya:multilingual",
      getTimeoutMs: () => 3000,
      fallback: fallback.fn,
    });

    expect(() => classify(["a"])).not.toThrow();
    expect(classify(["a"])).toEqual(["neutral"]);
  }, 10_000);
});
