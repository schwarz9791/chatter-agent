/**
 * Ollaya（ローカルの Jev 互換 decision model ランタイム、`ollaya.dev`）へ、1文ずつ
 * `/v1/systemone` の score（`noul`）で問い合わせる感情分類器。
 *
 * ★ **CLI（chatter-agent-speak）は同期実行**なので、Node の `fetch`（非同期）はメイン
 *   プロセスの中では直接使えない。1メッセージぶんの文をまとめて子プロセス（`spawnSync`）に
 *   渡し、子の中で非同期に問い合わせて結果をまとめて返すことで、プロセス起動のコストを
 *   メッセージ単位に抑える（依存を増やさない。curl には頼らない）。
 * ★ 判定の指示と基準は英語で書く（本文自体は日本語のまま渡す）。日本語で書くと精度が落ちる。
 * ★ どの失敗（接続拒否・タイムアウト・壊れた応答）でも例外を投げず、渡された `fallback`
 *   （辞書式）に委ねる。子プロセス全体が失敗すれば全文を、一部の文だけ壊れていればその文
 *   だけを fallback する。
 */

import { spawnSync } from "child_process";
import type { Emotion } from "../core/types";

const EMOTION_KEYS: readonly Emotion[] = ["happy", "relaxed", "surprised", "sad", "angry", "neutral"];

/**
 * 感情ごとの判定基準（英語）。sad / angry はコーディングエージェント自身の状況に
 * 当てはめて判定させる（作業の手戻りや謝罪を拾うため）。
 */
const EMOTION_DESC: Record<Emotion, string> = {
  happy: "A report that something went well: finished work, tests passing, review feedback addressed.",
  relaxed: "Waiting for a result, doing a routine check, or calmly describing the current state.",
  surprised: "An unexpected discovery: a fact or bug nobody anticipated, told with a sense of surprise.",
  sad:
    "Something did not go as hoped. Examples: a sub-agent has not reported back, work had to be " +
    'sent back, an apology ("I\'m sorry"), or realizing something was overlooked.',
  angry:
    "The agent's own failure or an unexpected breakdown. Examples: a test failed unexpectedly, " +
    "the agent's own change caused a regression, or the same mistake was made twice.",
  neutral: "A flat statement of fact with no emotional ups or downs.",
};

/**
 * 子プロセス（Node、CommonJS として `node -e` に渡す）の中で実行するスクリプト。
 * stdin から `{ texts, baseUrl, model }` を読み、stdout に `(Record<Emotion, number> | null)[]` を
 * JSON で書く。壊れた応答・接続エラーはその文だけ `null` にして続行する（1文の失敗で残りを
 * 諦めない）。
 *
 * ★ テンプレートリテラルの入れ子を避けるため、文字列連結だけで組んである（コード生成時の
 *   エスケープ事故を避けるため）。
 */
const CHILD_SCRIPT = [
  'const fs = require("fs");',
  `const KEYS = ${JSON.stringify(EMOTION_KEYS)};`,
  `const DESC = ${JSON.stringify(EMOTION_DESC)};`,
  "function payloadFor(model, text) {",
  "  const questions = {};",
  "  for (const k of KEYS) {",
  '    var suffix = (k === "sad" || k === "angry")',
  '      ? " Sad and angry tend to score low; when the criteria fit, score them true without hesitation."',
  '      : "";',
  "    questions[k] = {",
  '      type: "noul",',
  '      instructions: "Does this remark by a coding agent carry the following emotion? The utterance is in Japanese. \\"" + k + "\\": " + DESC[k] + "." + suffix,',
  '      criteria: { true: "present", false: "not present" },',
  "    };",
  "  }",
  "  return { model: model, state: text, questions: questions };",
  "}",
  "async function classifyOne(baseUrl, model, text) {",
  '  const res = await fetch(baseUrl + "/v1/systemone", {',
  '    method: "POST",',
  '    headers: { "Content-Type": "application/json" },',
  "    body: JSON.stringify(payloadFor(model, text)),",
  "  });",
  "  if (!res.ok) return null;",
  "  const obj = await res.json();",
  "  const scores = {};",
  "  for (const k of KEYS) {",
  "    const v = obj && obj.answers && obj.answers[k] ? obj.answers[k].noul : undefined;",
  '    if (typeof v !== "number") return null;',
  "    scores[k] = v;",
  "  }",
  "  return scores;",
  "}",
  "(async () => {",
  '  const input = JSON.parse(fs.readFileSync(0, "utf-8"));',
  "  const out = [];",
  "  for (const text of input.texts) {",
  "    try {",
  "      out.push(await classifyOne(input.baseUrl, input.model, text));",
  "    } catch (e) {",
  "      out.push(null);",
  "    }",
  "  }",
  "  process.stdout.write(JSON.stringify(out));",
  "})();",
].join("\n");

function argmaxEmotion(scores: Record<string, number> | null): Emotion | null {
  if (!scores) return null;
  let best: Emotion | null = null;
  let bestValue = Number.NEGATIVE_INFINITY;
  for (const key of EMOTION_KEYS) {
    const v = scores[key];
    if (typeof v === "number" && Number.isFinite(v) && v > bestValue) {
      bestValue = v;
      best = key;
    }
  }
  return best;
}

export interface OllayaEmotionClassifierDeps {
  getBaseUrl: () => string;
  getModel: () => string;
  /** 判定1回（メッセージ単位。子プロセス1回ぶん）の上限。超えたら fallback */
  getTimeoutMs: () => number;
  /** 接続不可・タイムアウト・壊れた応答のときのフォールバック（辞書式） */
  fallback: (texts: string[]) => Emotion[];
  /** テスト用。既定 `child_process.spawnSync` */
  spawnSyncFn?: typeof spawnSync;
}

/**
 * `(texts: string[]) => Emotion[]` を作る。1文ずつ Ollaya に問い合わせるが、
 * プロセス起動は `texts` 全体で1回にまとめる。throw しない。
 */
export function createOllayaEmotionClassifier(deps: OllayaEmotionClassifierDeps): (texts: string[]) => Emotion[] {
  const spawnSyncFn = deps.spawnSyncFn ?? spawnSync;

  return (texts) => {
    if (texts.length === 0) return [];

    let stdout: string;
    try {
      const result = spawnSyncFn(process.execPath, ["-e", CHILD_SCRIPT], {
        input: JSON.stringify({ texts, baseUrl: deps.getBaseUrl(), model: deps.getModel() }),
        encoding: "utf-8",
        timeout: deps.getTimeoutMs(),
        killSignal: "SIGKILL",
        maxBuffer: 8 * 1024 * 1024,
      });
      if (result.error || result.status !== 0 || !result.stdout) return deps.fallback(texts);
      stdout = result.stdout;
    } catch {
      return deps.fallback(texts);
    }

    let parsed: unknown;
    try {
      parsed = JSON.parse(stdout);
    } catch {
      return deps.fallback(texts);
    }
    if (!Array.isArray(parsed) || parsed.length !== texts.length) return deps.fallback(texts);

    const emotions = (parsed as unknown[]).map((scores) => argmaxEmotion(scores as Record<string, number> | null));
    const brokenIndices: number[] = [];
    emotions.forEach((e, i) => {
      if (e === null) brokenIndices.push(i);
    });
    if (brokenIndices.length === 0) return emotions as Emotion[];

    // 一部の文だけ壊れた応答だった場合は、その文だけ辞書式で埋める（全文を捨てない）
    const brokenTexts = brokenIndices.map((i) => texts[i]!);
    const brokenEmotions = deps.fallback(brokenTexts);
    const out = emotions.slice() as Emotion[];
    brokenIndices.forEach((i, idx) => {
      out[i] = brokenEmotions[idx] ?? "neutral";
    });
    return out;
  };
}
