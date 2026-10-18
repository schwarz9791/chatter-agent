/**
 * Ollaya（ローカルの Jev 互換 decision model ランタイム、`ollaya.dev`）へ、1文ずつ
 * `/v1/systemone` の choice で問い合わせる感情分類器。
 *
 * ★ 2段階で聞く。1段目で `GROUP_A` と `GROUP_B` からそれぞれ1つ選ばせ、2段目でその2つと
 *   neutral から選ばせる。6感情を一度に選ばせるより、平叙文が surprised や sad に流れにくい。
 * ★ 2段目の最上位の確率が下限（`EMOTION_FLOOR`）に届かなければ neutral に倒す。人はいつも
 *   感情を表に出しているわけではないので、確信が持てないときの既定を neutral 側に置く。
 * ★ `GROUP_A` の感情は、1段目で過半を取れていなければ neutral に倒す（`GROUP_A_MAJORITY`）。
 *   快・不快の向きで迷った文は、2段目で候補が減ると確率が膨らみ、失敗の報告を happy と
 *   言い切るような逆の感情になる。
 * ★ 判定基準（`CRITERIA`）は感情そのもの（嬉しい・驚き・詫び・苛立ち）を軸に書く。
 *   出来事の種類（「予期しない事実」等）で書くと、ただの説明文までその出来事の感情に
 *   引き寄せられる。
 * ★ 選択肢の説明は短く保つ（長いと判定時に切り詰められる）。
 * ★ **CLI（chatter-agent-speak）は同期実行**なので、`fetch`（非同期）はメインプロセスの
 *   中では直接使えない。1メッセージぶんの文をまとめて子プロセス（`spawnSync`）に渡し、
 *   子の中で非同期に問い合わせて結果をまとめて返すことで、プロセス起動のコストをメッセージ
 *   単位に抑える（依存を増やさない。curl には頼らない）。子は自分自身を隠しサブコマンド
 *   `ollaya-child` で起こす。単一バイナリでも bun の CLI としては動かさないので、cwd の
 *   `bunfig.toml` や `.env` を読まないという、親と同じ前提のまま立ち上がる。
 * ★ 判定の指示と基準は英語で書く。日本語で書くと精度が落ちる。本文の言語は問わない。
 * ★ どの失敗（接続拒否・タイムアウト・壊れた応答）でも例外を投げず、渡された `fallback`
 *   （辞書式）に委ねる。子プロセス全体が失敗すれば全文を、一部の文だけ壊れていればその文
 *   だけを fallback する。
 */

import { spawnSync } from "child_process";
import * as fs from "fs";
import { pickEmotion } from "./emotionScores";
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

/** 1段目で比べさせる組。似た向きの感情どうしを先に比べさせ、勝者だけを neutral と比べる。 */
const GROUP_A: readonly Emotion[] = ["happy", "angry", "sad"];
const GROUP_B: readonly Emotion[] = ["relaxed", "surprised"];

/** 2段目の最上位の確率がこれに届かなければ neutral として扱う。 */
const EMOTION_FLOOR = 0.8;
/**
 * relaxed だけの下限。relaxed は穏やかで確率が高く出にくく、一方で誤っても neutral と
 * 見分けがつきにくい表情なので、他の感情より緩める。
 */
const RELAXED_FLOOR = 0.5;
/** `GROUP_A` の感情が1段目で取るべき確率。3択の過半に届かなければ、向きを迷っているとみなす。 */
const GROUP_A_MAJORITY = 0.5;

/** 子プロセスが1文ごとに返すもの。`first` は1段目の勝者2つの確率、`final` は2段目の確率。 */
interface StageScores {
  first: Record<string, number>;
  final: Record<string, number>;
}

function choice(keys: readonly Emotion[]): Record<string, unknown> {
  const criteria: Record<string, string> = {};
  for (const k of keys) criteria[k] = CRITERIA[k];
  return { type: "choice", instructions: EMOTION_QUESTION_INSTRUCTIONS, criteria };
}

async function ask(
  baseUrl: string,
  model: string,
  text: string,
  questions: object,
): Promise<Record<string, unknown> | null> {
  const res = await fetch(baseUrl + "/v1/systemone", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ model, state: text, questions }),
  });
  if (!res.ok) return null;
  const obj = (await res.json()) as { answers?: Record<string, unknown> } | null;
  return obj?.answers ?? null;
}

function probs(answer: unknown, keys: readonly string[]): Record<string, number> | null {
  const p = (answer as { probabilities?: unknown } | null | undefined)?.probabilities;
  if (typeof p !== "object" || p === null) return null;
  const rec = p as Record<string, unknown>;
  for (const k of keys) {
    if (typeof rec[k] !== "number") return null;
  }
  return rec as Record<string, number>;
}

function top(p: Record<string, number>, keys: readonly Emotion[]): Emotion {
  let best = keys[0]!;
  for (const k of keys) {
    if (p[k]! > p[best]!) best = k;
  }
  return best;
}

async function classifyOne(baseUrl: string, model: string, text: string): Promise<StageScores | null> {
  const s1 = await ask(baseUrl, model, text, { a: choice(GROUP_A), b: choice(GROUP_B) });
  const pa = probs(s1?.a, GROUP_A);
  const pb = probs(s1?.b, GROUP_B);
  if (!pa || !pb) return null;
  const wa = top(pa, GROUP_A);
  const wb = top(pb, GROUP_B);
  const finalKeys: Emotion[] = [wa, wb, "neutral"];
  const s2 = await ask(baseUrl, model, text, { final: choice(finalKeys) });
  const pf = probs(s2?.final, finalKeys);
  if (!pf) return null;
  const first: Record<string, number> = { [wa]: pa[wa]!, [wb]: pb[wb]! };
  const final: Record<string, number> = {};
  for (const k of finalKeys) final[k] = pf[k]!;
  return { first, final };
}

/**
 * 1文ずつ Ollaya に問い合わせる。壊れた応答・接続エラーはその文だけ `null` にして続行する
 * （1文の失敗で残りを諦めない）。
 */
export async function classifyAll(input: {
  texts: string[];
  baseUrl: string;
  model: string;
}): Promise<(StageScores | null)[]> {
  const out: (StageScores | null)[] = [];
  for (const text of input.texts) {
    try {
      out.push(await classifyOne(input.baseUrl, input.model, text));
    } catch {
      out.push(null);
    }
  }
  return out;
}

/**
 * 子プロセスの本体。stdin から `{ texts, baseUrl, model }` を読み、stdout に
 * `(StageScores | null)[]` を JSON で書く。`process.exit` は呼ばない（パイプへの書き込みを
 * 流しきってから自然に終わらせる）。
 */
export async function runOllayaChild(): Promise<void> {
  const input = JSON.parse(fs.readFileSync(0, "utf-8")) as { texts: string[]; baseUrl: string; model: string };
  process.stdout.write(JSON.stringify(await classifyAll(input)));
}

/**
 * 自分自身を `ollaya-child` で起こすコマンド。単一バイナリでは `argv[1]` が埋め込みの仮想パス
 * （`/$bunfs/...`）になるので、スクリプトを挟まず実行ファイルへ直接サブコマンドを渡す。
 */
export function selfCommand(execPath: string, argv: readonly string[]): [string, string[]] {
  const script = argv[1] ?? "";
  if (script.startsWith("/$bunfs/")) return [execPath, ["ollaya-child"]];
  return [execPath, [script, "ollaya-child"]];
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

/** 2段目の最上位の感情を選び、確信が足りなければ neutral にする。壊れていれば `null`。 */
function decide(result: unknown): Emotion | null {
  if (typeof result !== "object" || result === null) return null;
  const { first, final } = result as Partial<StageScores>;
  if (typeof first !== "object" || first === null) return null;
  const top = pickEmotion(final);
  if (top === null || top === "neutral") return top;
  if (GROUP_A.includes(top) && !(first[top]! >= GROUP_A_MAJORITY)) return "neutral";
  const floor = top === "relaxed" ? RELAXED_FLOOR : EMOTION_FLOOR;
  return final![top]! >= floor ? top : "neutral";
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
      const [cmd, args] = selfCommand(process.execPath, process.argv);
      const result = spawnSyncFn(cmd, args, {
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

    const emotions = (parsed as unknown[]).map(decide);
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
