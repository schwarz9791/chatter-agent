#!/bin/bash
# Phase A の受け入れ確認。
#
# plugin/ がまだ無いので、bash hook の代わりに spool を手で置いて CLI を叩く。
# 実機での確認（ターミナル表示と体感で同時か）は plugin/ 着手時に行う。
#
#   cd core && npm run build && npm run verify:phase-a
#
# 使い捨ての XDG_CONFIG_HOME を掘るので、実際の ~/.config/chatter-agent は汚さない。
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
CLI="$REPO/plugin/bin/chatter-agent-speak.mjs"

if [ ! -f "$CLI" ]; then
  echo "バンドルがありません: $CLI" >&2
  echo "先に core/ で npm run build を実行してください。" >&2
  exit 1
fi

ROOT=$(mktemp -d)
trap 'rm -rf "$ROOT"' EXIT
export XDG_CONFIG_HOME="$ROOT"
RUNTIME="$ROOT/chatter-agent"
SPOOL="$RUNTIME/spool"
LOG="$RUNTIME/speech.jsonl"
QUEUE="$RUNTIME/speech"
mkdir -p "$SPOOL"

# MessageDisplay の実測ペイロード（設計書 §2-3）を1行組み立てて spool に追記し、CLI を起動する
delta() { # message_id index final delta
  node -e '
    const [m, i, f, d] = process.argv.slice(1);
    console.log(JSON.stringify({
      session_id: "sess-1", transcript_path: "/tmp/x.jsonl", cwd: "/tmp", prompt_id: "p1",
      hook_event_name: "MessageDisplay", turn_id: "turn-1",
      message_id: m, index: Number(i), final: f === "true", delta: d,
    }));
  ' "$1" "$2" "$3" "$4" >> "$SPOOL/$1.jsonl"
  node "$CLI"
}

prompt() { # file-suffix json
  printf '%s' "$2" > "$SPOOL/prompt-$1.json"
  node "$CLI"
}

show() { printf '\n\033[1m--- %s ---\033[0m\n' "$1"; }
spoken() {
  if [ ! -f "$LOG" ]; then echo "(まだ何も無い)"; return; fi
  node -e '
    const fs = require("fs");
    for (const l of fs.readFileSync(process.argv[1], "utf8").split("\n").filter(Boolean)) {
      const r = JSON.parse(l);
      console.log(`seq=${String(r.seq).padStart(2)} ${r.kind.padEnd(9)} ${JSON.stringify(r.text)}`);
    }' "$LOG"
}

show "① ツール完了を待たずに、確定した文だけが1文1行で出る"
delta m-aaa 0 false "完全に判明しました。ストリーミングでテキストが流れてきます。"
echo "[delta 0 の直後 / final はまだ来ていない]"; spoken
delta m-aaa 1 false "対照の PostToolUse も2件出たので、設定が読み込まれたことは確定です。"
echo "[delta 1 の直後]"; spoken

show "② 未閉じコードブロックの中身は読み上げない"
delta m-aaa 2 false $'こう書きます。\n```ts\nconst secret = 1;\n'
spoken

show "③ 別メッセージが始まると、前メッセージの保留中の最終文が先に流れる"
echo "[m-aaa の final はまだ 34〜80 秒先。ここで m-bbb が始まる]"
delta m-bbb 0 false "次の作業に入ります。ログを確認します。"
spoken

show "④ 応答待ち通知（PreToolUse に付随する同一 prompt_id の Notification は捨てる）"
prompt q1 '{"session_id":"sess-1","prompt_id":"p9","hook_event_name":"PreToolUse","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"次は何をしますか？","options":[{"label":"進める"},{"label":"やり直す"}]}]}}'
prompt n1 '{"session_id":"sess-1","prompt_id":"p9","hook_event_name":"Notification","message":"Claude needs your permission"}'
spoken

show "⑤ 34〜80 秒遅れて届く final:true。フェンスが閉じてブロックが消え、最後の文だけが出る"
delta m-aaa 3 true $'const secret = 1;\n```\nこれは実装方針に関わる分岐があるので確認させてください。'
spoken

show "⑥ 並列に叩いても seq が飛ばず、順序も壊れない"
for i in 0 1 2 3 4 5; do
  node -e '
    const [m, i] = process.argv.slice(1);
    console.log(JSON.stringify({ session_id: "sess-1", hook_event_name: "MessageDisplay",
      turn_id: "turn-1", message_id: m, index: Number(i), final: false, delta: `並列${i}です。` }));
  ' m-ccc "$i" >> "$SPOOL/m-ccc.jsonl"
done
for _ in 1 2 3 4 5 6 7 8; do node "$CLI" & done; wait
delta m-ccc 6 true "終わり。"
spoken

show "結果の検証"
node -e '
  const fs = require("fs");
  const rows = fs.readFileSync(process.argv[1], "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l));
  const seqs = rows.map((r) => r.seq);
  const texts = rows.map((r) => r.text);
  let failed = 0;
  const check = (label, ok) => {
    if (!ok) failed++;
    console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  ${label}`);
  };

  check("seq が 1 から欠番なく連続", seqs.every((s, i) => s === i + 1));
  check("重複した発話が無い", new Set(texts).size === texts.length);
  check("コードが漏れていない", !texts.some((t) => t.includes("const secret")));
  check("未閉じフェンスの記号が漏れていない", !texts.some((t) => t.includes("```")));
  check("付随 Notification は捨てられている", !texts.includes("Claude needs your permission"));
  check("質問と選択肢が読み上げられている", texts.some((t) => t.includes("選択肢は、進める、やり直す")));
  check("遅れて届いた final の最後の文が出ている", texts.includes("これは実装方針に関わる分岐があるので確認させてください。"));
  check("既に流した文が final で再送されていない", texts.filter((t) => t === "こう書きます。").length === 1);
  check("並列起動でも順序が保たれている",
    JSON.stringify(texts.filter((t) => t.startsWith("並列"))) ===
    JSON.stringify([0, 1, 2, 3, 4, 5].map((i) => `並列${i}です。`)));
  check("spool は片付いている（final 済みのファイルが残っていない）",
    !fs.readdirSync(process.argv[2]).some((f) => f.startsWith("m-aaa") || f.startsWith("m-ccc") || f.startsWith("prompt-")));

  // ★ ⑥ の並列起動チェック。ロックが破れたときの失敗モードは、speech.jsonl の重複行
  //   （見える）から speech/<seq>.json の上書き（無言で消える）に移った。$LOG と $SPOOL
  //   だけでは後者を検出できないので、配信キューも見る
  const queueDir = process.argv[3];
  const queueFiles = fs.readdirSync(queueDir);
  const queueJson = queueFiles.filter((f) => f.endsWith(".json"));
  const queueSeqs = queueJson.map((f) => Number(f.slice(0, -".json".length))).sort((a, b) => a - b);

  check("speech.jsonl と配信キューの seq 集合が一致する（キュー entry が上書きで潰れていない）",
    JSON.stringify(queueSeqs) === JSON.stringify([...seqs].sort((a, b) => a - b)));

  const seqMismatch = queueJson.some((f) => {
    const fileSeq = Number(f.slice(0, -".json".length));
    const payload = JSON.parse(fs.readFileSync(`${queueDir}/${f}`, "utf8"));
    return payload.seq !== fileSeq;
  });
  check("各キューファイルの payload の seq がファイル名と食い違っていない", !seqMismatch);

  check(".json.tmp が残っていない（tmp + rename が途中で終わっていない）",
    !queueFiles.some((f) => f.endsWith(".json.tmp")));

  process.exit(failed === 0 ? 0 : 1);
' "$LOG" "$SPOOL" "$QUEUE"
