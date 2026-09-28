/**
 * Ollaya（ローカルの Jev 互換 decision model ランタイム、`ollaya.dev`）へ、1文ずつ
 * `/v1/systemone` の choice で問い合わせる感情分類器。
 *
 * ★ 6感情を1問の choice で聞く。感情ごとに独立に「乗っているか」を聞くと、neutral は
 *   平叙文でも低く出て、argmax（`pickEmotion`）ではまず選ばれない。
 * ★ 最上位の感情の確率が `NEUTRAL_FLOOR` に届かなければ neutral に倒す。人はいつも感情を
 *   表に出しているわけではないので、確信が持てないときの既定を neutral 側に置く。
 * ★ 判定基準（`CRITERIA`）は感情そのもの（嬉しい・驚き・詫び・苛立ち）を軸に書く。
 *   出来事の種類（「予期しない事実」等）で書くと、ただの説明文までその出来事の感情に
 *   引き寄せられる。
 * ★ 選択肢の説明は短く保つ（長いと判定時に切り詰められる）。
 * ★ **CLI（chatter-agent-speak）は同期実行**なので、Node の `fetch`（非同期）はメイン
 *   プロセスの中では直接使えない。1メッセージぶんの文をまとめて子プロセス（`spawnSync`）に
 *   渡し、子の中で非同期に問い合わせて結果をまとめて返すことで、プロセス起動のコストを
 *   メッセージ単位に抑える（依存を増やさない。curl には頼らない）。
 * ★ 判定の指示と基準は英語で書く。日本語で書くと精度が落ちる。本文の言語は問わない。
 * ★ どの失敗（接続拒否・タイムアウト・壊れた応答）でも例外を投げず、渡された `fallback`
 *   （辞書式）に委ねる。子プロセス全体が失敗すれば全文を、一部の文だけ壊れていればその文
 *   だけを fallback する。
 */

import { spawnSync } from "child_process";
import { EMOTION_KEYS, pickEmotion } from "./emotionScores";
import type { Emotion } from "../core/types";

/** choice の選択肢（英語）。 */
const CRITERIA: Record<Emotion, string> = {
  happy: "Glad that something went well: work finished, tests passing.",
  relaxed: "Calmly waiting for a result, or relieved after finishing something.",
  surprised: "Astonished by an unexpected result or bug.",
  sad: "Sorry or disappointed: an apology, rework, or a mistake noticed.",
  angry: "Frustrated at its own failure: a broken test, a regression, a repeated mistake.",
  neutral: "No particular emotion: a routine progress report, plan, question, or plain explanation.",
};

const EMOTION_QUESTION_INSTRUCTIONS = "Which emotion does this remark by a coding agent express?";

/** 最上位の感情の確率がこれに届かなければ neutral として扱う。 */
const NEUTRAL_FLOOR = 0.6;

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
  `const CRITERIA = ${JSON.stringify(CRITERIA)};`,
  `const INSTRUCTIONS = ${JSON.stringify(EMOTION_QUESTION_INSTRUCTIONS)};`,
  "function payloadFor(model, text) {",
  "  return {",
  "    model: model,",
  "    state: text,",
  "    questions: {",
  '      emotion: { type: "choice", instructions: INSTRUCTIONS, criteria: CRITERIA },',
  "    },",
  "  };",
  "}",
  "async function classifyOne(baseUrl, model, text) {",
  '  const res = await fetch(baseUrl + "/v1/systemone", {',
  '    method: "POST",',
  '    headers: { "Content-Type": "application/json" },',
  "    body: JSON.stringify(payloadFor(model, text)),",
  "  });",
  "  if (!res.ok) return null;",
  "  const obj = await res.json();",
  "  const probs = obj && obj.answers && obj.answers.emotion ? obj.answers.emotion.probabilities : undefined;",
  '  if (typeof probs !== "object" || probs === null) return null;',
  "  const scores = {};",
  "  for (const k of KEYS) {",
  "    const v = probs[k];",
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

/** neutral の確率を `NEUTRAL_FLOOR` まで底上げする。argmax がそのまま「届かなければ neutral」になる。 */
function applyNeutralFloor(scores: Record<string, number> | null): Record<string, number> | null {
  if (scores === null || typeof scores.neutral !== "number") return scores;
  return { ...scores, neutral: Math.max(scores.neutral, NEUTRAL_FLOOR) };
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

    const emotions = (parsed as unknown[]).map((scores) =>
      pickEmotion(applyNeutralFloor(scores as Record<string, number> | null)),
    );
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
