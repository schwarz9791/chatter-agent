import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createFmEmotionClassifier, writeFmEmotionSchemaIfAbsent, FM_EMOTION_SCHEMA } from "./fmClassifier";
import type { Emotion } from "../core/types";

let dir: string;
let binDir: string;
let schemaPath: string;
let homeDir: string;
let originalPath: string | undefined;

/**
 * `findCommandPath("fm")` は実 `process.env.PATH` を読む（core/commandPath.ts はテスト用の
 * env 差し替え口を持つが、fmClassifier.ts はそれを公開していない）。ここでは一時ディレクトリを
 * PATH の先頭に足して「fm という名前のコマンド」をテストごとに用意する。
 */
function installFakeFm(script: string): void {
  const file = path.join(binDir, "fm");
  fs.writeFileSync(file, script);
  fs.chmodSync(file, 0o755);
  process.env.PATH = `${binDir}${path.delimiter}${originalPath ?? ""}`;
}

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-fm-classifier-"));
  binDir = path.join(dir, "bin");
  fs.mkdirSync(binDir);
  schemaPath = path.join(dir, "emotion-schema.json");
  homeDir = path.join(dir, "home");
  originalPath = process.env.PATH;
});

afterEach(() => {
  process.env.PATH = originalPath;
  fs.rmSync(dir, { recursive: true, force: true });
});

function makeFallback(): { fn: (texts: string[]) => Emotion[]; calls: string[][] } {
  const calls: string[][] = [];
  const fn = (texts: string[]): Emotion[] => {
    calls.push(texts);
    return texts.map(() => "neutral");
  };
  return { fn, calls };
}

describe("createFmEmotionClassifier", () => {
  it("スキーマ強制の応答から最大値のラベルを全文に適用する", () => {
    installFakeFm(
      `#!/usr/bin/env node\nprocess.stdout.write(JSON.stringify({ happy: 0.9, relaxed: 0.1, surprised: 0.1, sad: 0.05, angry: 0.02, neutral: 0.1 }));\n`,
    );
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    const result = classify(["やりました！", "無事に終わりました。"]);
    expect(result).toEqual(["happy", "happy"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("コードフェンス付きの応答でも剥がして読む", () => {
    installFakeFm(
      '#!/usr/bin/env node\nprocess.stdout.write("```json\\n" + JSON.stringify({ happy: 0.1, relaxed: 0.1, surprised: 0.1, sad: 0.9, angry: 0.1, neutral: 0.1 }) + "\\n```");\n',
    );
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["残念です。"])).toEqual(["sad"]);
  });

  it("初回だけスキーマ JSON を書き出し、以後は上書きしない", () => {
    installFakeFm(
      `#!/usr/bin/env node\nprocess.stdout.write(JSON.stringify({ happy: 0, relaxed: 0, surprised: 0, sad: 0, angry: 0, neutral: 1 }));\n`,
    );
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      getTimeoutMs: () => 5000,
      fallback: (texts) => texts.map(() => "neutral"),
    });

    classify(["確認します。"]);
    expect(fs.existsSync(schemaPath)).toBe(true);
    expect(JSON.parse(fs.readFileSync(schemaPath, "utf-8"))).toEqual(FM_EMOTION_SCHEMA);

    fs.writeFileSync(schemaPath, "触られていないことを確かめる目印");
    writeFmEmotionSchemaIfAbsent(schemaPath);
    expect(fs.readFileSync(schemaPath, "utf-8")).toBe("触られていないことを確かめる目印");
  });

  it("コマンドが見つからなければ fallback へ落ちる", () => {
    process.env.PATH = "";
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["a", "b"])).toEqual(["neutral", "neutral"]);
    expect(fallback.calls).toEqual([["a", "b"]]);
  });

  it("非ゼロ終了は fallback へ落ちる", () => {
    installFakeFm("#!/usr/bin/env node\nprocess.exit(1);\n");
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["x"])).toEqual(["neutral"]);
    expect(fallback.calls).toEqual([["x"]]);
  });

  it("壊れた JSON は fallback へ落ちる", () => {
    installFakeFm('#!/usr/bin/env node\nprocess.stdout.write("not json");\n');
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["x"])).toEqual(["neutral"]);
  });

  it("タイムアウトは fallback へ落ちる（例外を投げない）", () => {
    installFakeFm("#!/usr/bin/env node\nsetInterval(() => {}, 1000);\n");
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({ schemaPath, homeDir, getTimeoutMs: () => 200, fallback: fallback.fn });

    expect(() => classify(["x"])).not.toThrow();
    expect(classify(["x"])).toEqual(["neutral"]);
  }, 10_000);

  it("空配列は fm を起動せずに空配列を返す", () => {
    process.env.PATH = "";
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify([])).toEqual([]);
    expect(fallback.calls).toHaveLength(0);
  });
});
