/**
 * 設定パネルの「テスト要約」（#76 の `POST /v1/summary/preview`）。
 *
 * ★★ **`createSummaryPipeline` をそのまま回さないこと。** issue #76 は
 *   「`isEnabled: () => true` / `getMaxPerDrain: () => 1` で1回だけ回す」と書いているが、
 *   あれは `execFileSync` で**イベントループを最大 `aiSummaryTimeoutMs`（既定60秒）止める**。
 *   常駐サーバーでそれをやると、その間 WebSocket の配信も `GET /audio/…` も止まり、
 *   クライアント側の音声取得が `audioFetchTimeoutMs`（既定45秒）で転送エラーになって
 *   **試行回数を消費し、発話が捨てられる**（→ `docs/protocol.md` の責務8）。
 *   テストボタンを押しただけで発話が落ちる、という壊れ方になる。
 *
 * ★ **代わりに、判断を分けずに共有する。** ここが独自に持っているのは
 *   「非同期で1回だけ起こす」ことだけで、
 *
 *     - 引数        `buildSummaryArgs`
 *     - 環境変数     `buildSummaryEnv`（無限ループ防止の第1層 `CHATTER_AGENT_DISABLE=1` を含む）
 *     - 指示文       `SUMMARY_INSTRUCTION`
 *     - 採用の規則   `isAcceptableSummary`
 *     - 整形         `toSpeechSentences`
 *
 *   はすべて本番（`summaryPipeline.ts`）と**同じものを呼んでいる**。テストボタンが
 *   通るのに本番では原文が読み上げられる、という一番切り分けにくいズレを作らないため。
 *
 * ★ 逆に、ここが**わざと通らない**のは `isEnabled` / `aiSummaryThreshold` /
 *   `aiSummaryMaxPerDrain` の3つ。要約が既定 OFF のままでも、短い文でも、
 *   「CLI が動くか」を試せることがこのボタンの目的なので、そこは飛ばすのが正しい。
 */

import { randomUUID } from "crypto";
import { toSpeechSentences } from "../text/speechText";
import { findCommandPath } from "../core/commandPath";
import { buildSummaryArgs, runClaudeCliAsync } from "./claudeCli";
import { SUMMARY_INSTRUCTION } from "./prompt";
import { isAcceptableSummary } from "./summaryPipeline";
import type { SummaryOutcome } from "./types";

export interface SummaryPreviewDeps {
  /** 要約に使う CLI（`aiSummaryCommand`） */
  getCommand: () => string;
  /** `--model` に渡す値（`aiSummaryModel`）。空文字なら渡さない */
  getModel: () => string;
  /** 要約1回の上限（`aiSummaryTimeoutMs`） */
  getTimeoutMs: () => number;
  /** 要約 CLI の cwd（隔離ディレクトリ。`getSummarizerHomeDir()`） */
  homeDir: string;
  /**
   * `--session-id` を、CLI を起こす**前に**永続化する（無限ループ防止の第2層）。
   *
   * ★ **省略可にしないこと。** ここを黙って飛ばすと、要約 CLI 自身の `MessageDisplay` hook の
   *   出力が spool に残ったとき、次のドレインでそれが読み上げられる。第1層
   *   （`CHATTER_AGENT_DISABLE=1`）が効いていれば起きないが、**第2層はその第1層が効かなかった
   *   ときのための保険**なので、片方だけ持つ形にしない（→ `core/summarizerSessions.ts`）。
   *
   * ★ **throw を握らないこと。** 登録できなければ CLI を起こさない、が安全側
   *   （`cli/worker.ts` の `registerSessionId` と同じ規律）。
   */
  registerSessionId: (sessionId: string) => void;
  /** テスト用。既定 `Date.now` */
  now?: () => number;
}

export interface SummaryPreviewResult {
  outcome: SummaryOutcome;
  /** 採用できた要約。**それ以外は `null`**（原文を返さない —— テストの答えとして紛らわしい） */
  summary: string | null;
  elapsedMs: number;
  /** 失敗の手がかり（stderr の抜粋）。成功なら空文字 */
  detail: string;
}

export async function runSummaryPreview(text: string, deps: SummaryPreviewDeps): Promise<SummaryPreviewResult> {
  const now = deps.now ?? Date.now;
  const startedAt = now();
  const done = (outcome: SummaryOutcome, summary: string | null, detail = ""): SummaryPreviewResult => ({
    outcome,
    summary,
    elapsedMs: now() - startedAt,
    detail,
  });

  let commandPath: string | undefined;
  try {
    commandPath = findCommandPath(deps.getCommand());
  } catch (err) {
    return done("internal", null, err instanceof Error ? err.message : String(err));
  }
  if (!commandPath) return done("no-command", null);

  // ★ 起こす前に登録する（→ `registerSessionId` の doc）。ここで throw されたら
  //   CLI を起こさずに internal で返す —— それが第2層の安全側の挙動
  const sessionId = randomUUID();
  try {
    deps.registerSessionId(sessionId);
  } catch (err) {
    return done("internal", null, err instanceof Error ? err.message : String(err));
  }

  const result = await runClaudeCliAsync({
    commandPath,
    args: buildSummaryArgs(SUMMARY_INSTRUCTION, { sessionId, model: deps.getModel() }),
    text,
    homeDir: deps.homeDir,
    timeoutMs: deps.getTimeoutMs(),
  });

  if (!result.ok) return done(result.reason, null, result.detail ?? "");

  const summary = result.stdout.trim();
  // 妥当性は「実際に読み上げる形」で判定する（本番と同じ規則。→ `isAcceptableSummary`）
  const spoken = toSpeechSentences(summary).join("\n");
  if (!isAcceptableSummary(spoken, text.length)) return done("invalid", null);

  // ★ 返すのは素の stdout（本番と同じ。整形は読み上げ経路が1箇所で行う）
  return done("ok", summary);
}
