/**
 * summarizer/ の型定義。
 *
 * cc-mascot の `services/summarizer/` を移植する際、バックエンド抽象化（`Summarizer` /
 * `BackendSpec` 等）は claude 専用化に伴って不要になったため持ち込んでいない。ここにあるのは
 * chatter-agent の呼び出し契約に合わせて新設した型と、`summaryPipeline.ts` / `claudeCli.ts` が
 * 内部で使う型だけ。
 */

/**
 * 呼び出し側（cli/worker.ts の DrainDeps）との契約。★ この signature を変えないこと。
 *
 * ★ **throw しない。** 失敗・タイムアウト・無効・閾値以下、どの経路でも原文をそのまま返す。
 *   呼び出し元は spool のドレイン中（`drainSpool`）に呼ぶ。ここで例外が漏れると、要約の失敗
 *   ごときで `processMessage` 全体が止まり、CLAUDE.md「絶対に守ること」1 で確定させた
 *   「final を待って必ず1回で出す」発話そのものが欠落する。
 *
 * @param text 要約対象の原文（`final:true` で確定したメッセージ全文）。
 * @param registerSessionId 要約 CLI に渡す `--session-id` を、`execFileSync` する **前に**
 *   呼び出し側へ渡して永続化させるためのコールバック。
 *   ★ 後に回してはいけない。要約中に親（`chatter-agent-speak`）が落ちると、次回起動時に
 *   このセッションIDが登録されないまま要約 CLI 自身の spool 出力（`MessageDisplay` hook 経由）
 *   が残ってしまい、それが次のドレインで読み上げられる（無限ループ防止の第2層が素通しになる）。
 */
export type Summarize = (text: string, registerSessionId: (sessionId: string) => void) => string;

/**
 * `summaryPipeline.ts` の判定結果。実測ログ（`logPath`）の2列目と1対1で対応する。
 *
 * - `ok`            要約を採用した
 * - `timeout`       CLI がタイムアウトで強制終了された
 * - `overflow`      stdout/stderr が `maxBuffer` を超えた（ENOBUFS）。timeout と誤報しない
 * - `error`         CLI が非ゼロ終了、または起動自体に失敗した（ENOENT 等）
 * - `invalid`       CLI は正常終了したが、出力が空 or 原文以上の長さ or 上限文字数超えだった（採用しない）
 * - `internal`      pipeline 内部の例外（getter の実装ミス、registerSessionId の失敗など）。
 *                    **CLI は起動していない。** `error`（CLI 起動後の失敗）と混同しないこと
 *                    （issue #38 レビュー D1(a)）
 * - `skipped-limit` このドレインでの要約実行回数が上限に達していた（CLI を起動していない）
 * - `no-command`    要約コマンドの絶対パスが解決できなかった（CLI を起動していない）
 */
export type SummaryOutcome =
  | "ok"
  | "timeout"
  | "overflow"
  | "error"
  | "invalid"
  | "internal"
  | "skipped-limit"
  | "no-command";

/**
 * `claudeCli.ts` の `runClaudeCli` の結果。
 *
 * ★ タイムアウト・maxBuffer 超過・その他のエラーを区別できる形にしてある。`execFileSync` は
 *   どれも同じく throw するので、呼び出し側（`runClaudeCli`）が `err.code` を見てここに振り分ける
 *   （`err.signal` の有無では判別できない。Node 24 実測では maxBuffer 超過でも
 *   `{ code: "ENOBUFS", signal: "SIGKILL" }` が付き、子プロセスの自死や外部からの kill で
 *   `SIGTERM` が付くこともある）。実測ログで「詰まっているのか」「溢れたのか」「壊れているのか」
 *   を見分けたいため。
 */
export type ClaudeCliResult =
  | { ok: true; stdout: string }
  | { ok: false; reason: "timeout"; detail?: string }
  | { ok: false; reason: "overflow"; detail: string }
  | { ok: false; reason: "error"; detail: string };
