import { describe, it, expect, beforeEach, afterEach } from "vitest";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { createFmEmotionClassifier, writeFmEmotionSchemaIfChanged, FM_EMOTION_SCHEMA } from "./fmClassifier";
import type { Emotion } from "../core/types";

let dir: string;
let binDir: string;
let schemaPath: string;
let homeDir: string;

function installFakeFm(script: string): string {
  const file = path.join(binDir, "fm");
  fs.writeFileSync(file, script);
  fs.chmodSync(file, 0o755);
  return file;
}

beforeEach(() => {
  dir = fs.mkdtempSync(path.join(os.tmpdir(), "chatter-agent-fm-classifier-"));
  binDir = path.join(dir, "bin");
  fs.mkdirSync(binDir);
  schemaPath = path.join(dir, "emotion-schema.json");
  homeDir = path.join(dir, "home");
});

afterEach(() => {
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
    const commandPath = installFakeFm(
      `#!/usr/bin/env node\nprocess.stdout.write(JSON.stringify({ happy: 0.9, relaxed: 0.1, surprised: 0.1, sad: 0.05, angry: 0.02, neutral: 0.1 }));\n`,
    );
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    const result = classify(["やりました！", "無事に終わりました。"]);
    expect(result).toEqual(["happy", "happy"]);
    expect(fallback.calls).toHaveLength(0);
  });

  it("コードフェンス付きの応答でも剥がして読む", () => {
    const commandPath = installFakeFm(
      '#!/usr/bin/env node\nprocess.stdout.write("```json\\n" + JSON.stringify({ happy: 0.1, relaxed: 0.1, surprised: 0.1, sad: 0.9, angry: 0.1, neutral: 0.1 }) + "\\n```");\n',
    );
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["残念です。"])).toEqual(["sad"]);
  });

  it("初回はスキーマ JSON を書き出す", () => {
    const commandPath = installFakeFm(
      `#!/usr/bin/env node\nprocess.stdout.write(JSON.stringify({ happy: 0, relaxed: 0, surprised: 0, sad: 0, angry: 0, neutral: 1 }));\n`,
    );
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath,
      getTimeoutMs: () => 5000,
      fallback: (texts) => texts.map(() => "neutral"),
    });

    classify(["確認します。"]);
    expect(fs.existsSync(schemaPath)).toBe(true);
    expect(JSON.parse(fs.readFileSync(schemaPath, "utf-8"))).toEqual(FM_EMOTION_SCHEMA);
  });

  /** ★ 内容が変わっていれば書き直す（将来 FM_EMOTION_SCHEMA が変わっても古いスキーマが残り続けない） */
  it("★ 中身が期待と違えば書き直す", () => {
    fs.mkdirSync(path.dirname(schemaPath), { recursive: true });
    fs.writeFileSync(schemaPath, "古いスキーマ（違う内容）");

    writeFmEmotionSchemaIfChanged(schemaPath);

    expect(JSON.parse(fs.readFileSync(schemaPath, "utf-8"))).toEqual(FM_EMOTION_SCHEMA);
  });

  /**
   * ★ 内容が既に同じなら書き直さない（不要な書き込みをしない）。
   *   意図的に古い mtime を設定してから呼ぶ。書き直せば mtime が今の時刻に変わるので、
   *   「触っていない」ことを確認できる（`fs.writeFileSync` は ESM の名前空間を差し替えられず
   *   spy できないため、mtime で観測する）。
   */
  it("★ 中身が既に同じなら書き直さない（mtime が変わらない）", () => {
    writeFmEmotionSchemaIfChanged(schemaPath); // 初回書き込み
    const past = new Date(Date.now() - 60_000);
    fs.utimesSync(schemaPath, past, past);

    writeFmEmotionSchemaIfChanged(schemaPath);

    expect(fs.statSync(schemaPath).mtime.getTime()).toBe(past.getTime());
  });

  it("コマンドが見つからなければ fallback へ落ちる", () => {
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath: path.join(dir, "no-such-fm"),
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["a", "b"])).toEqual(["neutral", "neutral"]);
    expect(fallback.calls).toEqual([["a", "b"]]);
  });

  it("非ゼロ終了は fallback へ落ちる", () => {
    const commandPath = installFakeFm("#!/usr/bin/env node\nprocess.exit(1);\n");
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["x"])).toEqual(["neutral"]);
    expect(fallback.calls).toEqual([["x"]]);
  });

  it("壊れた JSON は fallback へ落ちる", () => {
    const commandPath = installFakeFm('#!/usr/bin/env node\nprocess.stdout.write("not json");\n');
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["x"])).toEqual(["neutral"]);
  });

  /** ★ 全部0点・同点は先頭キー（happy）に偏らず neutral に倒す（→ emotion/emotionScores.ts） */
  it("★ スコアが全部0（または同点）なら neutral に倒す", () => {
    const commandPath = installFakeFm(
      `#!/usr/bin/env node\nprocess.stdout.write(JSON.stringify({ happy: 0, relaxed: 0, surprised: 0, sad: 0, angry: 0, neutral: 0 }));\n`,
    );
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath,
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify(["謝罪です。"])).toEqual(["neutral"]);
    expect(fallback.calls).toHaveLength(0); // fallback ではなく本物の判定結果として neutral
  });

  it("タイムアウトは fallback へ落ちる（例外を投げない）", () => {
    const commandPath = installFakeFm("#!/usr/bin/env node\nsetInterval(() => {}, 1000);\n");
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath,
      getTimeoutMs: () => 200,
      fallback: fallback.fn,
    });

    expect(() => classify(["x"])).not.toThrow();
    expect(classify(["x"])).toEqual(["neutral"]);
  }, 10_000);

  it("空配列は fm を起動せずに空配列を返す", () => {
    const fallback = makeFallback();
    const classify = createFmEmotionClassifier({
      schemaPath,
      homeDir,
      commandPath: path.join(dir, "no-such-fm"),
      getTimeoutMs: () => 5000,
      fallback: fallback.fn,
    });

    expect(classify([])).toEqual([]);
    expect(fallback.calls).toHaveLength(0);
  });
});
