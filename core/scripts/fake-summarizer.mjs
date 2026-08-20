#!/usr/bin/env node
/**
 * 要約 CLI（`claude`）の代わりに `aiSummaryCommand` へ差し込む偽の要約コマンド（issue #31 受け入れ確認用）。
 *
 * `fake-player.mjs` との違い: **shebang + 実行権限が要る。**
 * `fake-player.mjs` は `CHATTER_AGENT_PLAYER_COMMAND=process.execPath` + `CHATTER_AGENT_PLAYER_ARGS`
 * で常に `node fake-player.mjs <file>` として起動されるので実行ビットが要らない。
 * 一方、要約 CLI（`core/src/summarizer/claudeCli.ts` の `runClaudeCli`）は
 * `execFileSync(commandPath, args, ...)` で **このファイル自身を直接 exec する**
 * （node 経由でラップされない）。だから `#!/usr/bin/env node` と実行ビットの両方が要る。
 *
 * ★ 実行ビットは **`chmod +x` でこのファイルに直接、恒久的に**立ててある
 *   （`git ls-files -s` で 100755 になる）。`plugin/scripts/on-message.sh` や
 *   `verify-phase-a.sh` 自身と同じ扱い ── このリポジトリでは「直接 exec されるファイル」は
 *   git 管理下のファイルとして実行ビットを持たせ、`node <path>` と明示して起動する側
 *   （`fake-player.mjs` 側）だけを 644 のままにしている。`verify-phase-a.sh` 側で毎回
 *   `chmod +x` する方式にしなかったのは、そちらだと「実行ビットが無いのが平常状態」になり、
 *   本物の `claude`（＝実行可能なファイルへの絶対パス）という `aiSummaryCommand` の契約を
 *   この偽コマンドが体現しなくなるため。
 *
 * 受け取る引数は `core/src/summarizer/claudeCli.ts` の `buildSummaryArgs` が組み立てる並び
 * （`-p <指示文> --session-id <uuid> --no-session-persistence --strict-mcp-config
 * --disallowedTools <list> [--model <model>]`）だが、ここで見るのは `--session-id` の値だけ。
 * 原文は stdin で渡る（`runClaudeCli` の `input: deps.text`）。
 *
 * 環境変数:
 *   FAKE_SUMMARIZER_MODE    short（既定）: 固定の短い要約を stdout に返す / fail: stderr に書いて exit 1
 *   FAKE_SUMMARIZER_REPLY   short のときに返す文字列。既定は「（要約）短くなりました。」
 *   FAKE_SUMMARIZER_RECORD  受け取った内容を追記する記録先（1行1JSON、{sessionId, textLength}）。
 *                           未指定なら記録しない
 */

import * as fs from "node:fs";

// ★ stdin は必ず読み切ること。読まずに終了すると、書き込み側（execFileSync している親プロセス）
//   が EPIPE になりうる（plugin/scripts/_lib.sh の chatter_read_payload と同じ趣旨の注意）。
//   fd 0 を同期で読み切ってから残りの処理に入る。
const stdin = fs.readFileSync(0, "utf-8");

// --session-id の値だけを取り出す。buildSummaryArgs の並びに依存しない位置探索にしてある
// （将来引数の順序が変わっても、このファイルを直しに来なくて済む）
const args = process.argv.slice(2);
const sessionIdIndex = args.indexOf("--session-id");
const sessionId = sessionIdIndex >= 0 ? args[sessionIdIndex + 1] : null;

const recordPath = process.env.FAKE_SUMMARIZER_RECORD;
if (recordPath) {
  const line = JSON.stringify({ sessionId, textLength: stdin.length });
  fs.appendFileSync(recordPath, `${line}\n`);
}

const mode = process.env.FAKE_SUMMARIZER_MODE ?? "short";

if (mode === "fail") {
  console.error("FAKE_SUMMARIZER_MODE=fail で意図的に失敗しました");
  process.exitCode = 1;
} else {
  // summaryPipeline.ts の「7. 空 or 原文以上の長さ → 不採用」に引っかからないよう、
  // 既定値は十分短くしてある（原文は aiSummaryThreshold=200 文字超が前提のため）。
  // ★ この摘みで狙えるのは invalid（240文字上限）まで。overflow（maxBuffer 超過）は
  //   狙えない —— MAX_BUFFER_BYTES がちょうど1MiBで macOS の ARG_MAX と同値なため、
  //   環境変数でそれを超える文字列を渡そうとすると exec 自体が E2BIG で失敗する
  //  （Linux では MAX_ARG_STRLEN により環境変数1本が131072バイトに制限され、もっと手前で落ちる）
  const reply = process.env.FAKE_SUMMARIZER_REPLY ?? "（要約）短くなりました。";
  // ★ ここに `process.exit` を足さないこと。真因は `process.stdout.write` ではなく
  //   `process.exit` の側（実測: exit ありだと65536バイトで切れるが、exit を消すと全量届く）。
  //   macOS では stdout がパイプのときだけ書き込みが非同期になるため、write 直後に exit すると
  //   カーネルのパイプバッファ（64KiB）に収まらない分が flush 前に捨てられる。CI
  //  （.github/workflows/validate.yml は全ジョブ ubuntu-latest）はパイプでも同期書き込みなので、
  //   この切り詰めはそこでは検出できない。`fs.writeSync(1, ...)` で回避しようともしないこと
  //   —— `process.stdout` に一度でも触れる（console.log 等）と fd 1 に O_NONBLOCK が立ち、
  //   以後 `fs.writeSync` は書けた分を返すだけで例外を投げないため、かえって静かに切れる
  //  （実測で確認済み）。overflow.mjs（claudeCli.test.ts:376）と recorder.mjs
  //  （summaryPipeline.test.ts:66-68）も同じ流儀 —— 素の `process.stdout.write` を
  //   `process.exit` なしで使っている。stdin は読み切り済みで、他にイベントループを
  //   生かすハンドルは無いので、このまま自然終了する。
  process.stdout.write(reply);
}
