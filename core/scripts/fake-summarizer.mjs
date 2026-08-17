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
  process.exit(1);
}

// summaryPipeline.ts の「7. 空 or 原文以上の長さ → 不採用」に引っかからないよう、
// 既定値は十分短くしてある（原文は aiSummaryThreshold=200 文字超が前提のため）
const reply = process.env.FAKE_SUMMARIZER_REPLY ?? "（要約）短くなりました。";
process.stdout.write(reply);
process.exit(0);
