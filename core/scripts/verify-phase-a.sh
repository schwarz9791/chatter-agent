#!/bin/bash
# Phase A の受け入れ確認。
#
# **payload は実際の bash hook（plugin/scripts/*.sh）に食わせる。** 手で spool を組むと
# 「hook が spool に正しい形で置けるか」だけが検証の外に残り、そこが一番壊れやすい
# （message_id の抽出、agent_id の除外、final の delta が空のケース）。
#
# 実機での確認（ターミナル表示と体感で同時か）は別途行う。ここで見るのは形と順序だけ。
#
#   cd core && npm run build && npm run verify:phase-a
#
# 使い捨ての XDG_CONFIG_HOME を掘るので、実際の ~/.config/chatter-agent は汚さない。
set -euo pipefail

REPO=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
CLI="$REPO/plugin/bin/chatter-agent-speak.mjs"
ON_MESSAGE="$REPO/plugin/scripts/on-message.sh"
ON_PROMPT="$REPO/plugin/scripts/on-prompt.sh"

if [ ! -f "$CLI" ]; then
  echo "バンドルがありません: $CLI" >&2
  echo "先に core/ で npm run build を実行してください。" >&2
  exit 1
fi

for hook in "$ON_MESSAGE" "$ON_PROMPT"; do
  if [ ! -x "$hook" ]; then
    echo "hook が無いか実行権限がありません: $hook" >&2
    exit 1
  fi
done

ROOT=$(mktemp -d)
trap 'rm -rf "$ROOT"' EXIT
export XDG_CONFIG_HOME="$ROOT"
RUNTIME="$ROOT/chatter-agent"
SPOOL="$RUNTIME/spool"
LOG="$RUNTIME/speech.jsonl"
QUEUE="$RUNTIME/speech"
mkdir -p "$SPOOL"

# ★ hook にデタッチ起動をさせない。`nohup node … &` のままだと CLI がいつ走ったか分からず、
#   「delta を1つ積んだ直後の speech.jsonl」を見るこの検証が非決定的になる。
#   spool へ積むところまでを hook に、ドレインを下の `node "$CLI"` に分担させる。
export CHATTER_AGENT_CLI="$ROOT/detach-is-suppressed-here.mjs"

# MessageDisplay の実測ペイロード（設計書 §2-3）を組み立てて hook の stdin に流す。
# CLI は呼ばない
feed_message() { # message_id index final delta [extra-json]
  node -e '
    const [m, i, f, d, extra] = process.argv.slice(1);
    console.log(JSON.stringify({
      session_id: "sess-1", transcript_path: "/tmp/x.jsonl", cwd: "/tmp", prompt_id: "p1",
      hook_event_name: "MessageDisplay", turn_id: "turn-1",
      message_id: m, index: Number(i), final: f === "true", delta: d,
      ...(extra ? JSON.parse(extra) : {}),
    }));
  ' "$1" "$2" "$3" "$4" "${5-}" | "$ON_MESSAGE"
}

delta() { # message_id index final delta [extra-json]
  feed_message "$@"
  node "$CLI"
}

prompt() { # json
  printf '%s' "$1" | "$ON_PROMPT"
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
echo "[m-aaa の final はまだ先。ここで m-bbb が始まる]"
delta m-bbb 0 false "次の作業に入ります。ログを確認します。"
spoken

show "④ 応答待ち通知（PreToolUse に付随する同一 prompt_id の Notification は捨てる）"
prompt '{"session_id":"sess-1","prompt_id":"p9","hook_event_name":"PreToolUse","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"次は何をしますか？","options":[{"label":"進める"},{"label":"やり直す"}]}]}}'
prompt '{"session_id":"sess-1","prompt_id":"p9","hook_event_name":"Notification","message":"Claude needs your permission"}'
spoken

show "⑤ 大きく遅れて届く final:true。フェンスが閉じてブロックが消え、最後の文だけが出る"
delta m-aaa 3 true $'const secret = 1;\n```\nこれは実装方針に関わる分岐があるので確認させてください。'
spoken

show "⑥ 並列に叩いても seq が飛ばず、順序も壊れない"
for i in 0 1 2 3 4 5; do
  feed_message m-ccc "$i" false "並列${i}です。"
done
for _ in 1 2 3 4 5 6 7 8; do node "$CLI" & done; wait
delta m-ccc 6 true "終わり。"
spoken

show "⑦ hook 側のガード（ここが壊れると spool の形から崩れる）"

# delta 本文に "message_id" が出てくる。sed の貪欲マッチだと WRONG-ID を掴んでファイルが割れる
delta m-ddd 0 false 'JSON の "message_id":"WRONG-ID" について説明します。次に進みます。'

# サブエージェントの発言は捨てる（agent_id はサブエージェント内でのみ入る）
feed_message m-sub 0 true "サブエージェントの発言です。" '{"agent_id":"sub-1","agent_type":"Explore"}'

# message_id が無い / ファイル名に使えない値は捨てる
feed_message "" 0 true "message_id なしです。"
feed_message "../escape" 0 true "パストラバーサルです。"

# 全角英字（Ａｂｃ）は "." を含まないのでパストラバーサルとは別の壊れ方をする。
# chatter_safe_name は `case "$1" in *[!A-Za-z0-9_-]*)` でファイル名に使える文字だけを通す
# つもりだが、これはロケール依存の文字クラスマッチ。UTF-8 ロケール（例: en_US.UTF-8）だと
# 照合順序の都合で全角英数字が [A-Za-z0-9_-] の範囲に「入ってしまい」拒否できない
# （_lib.sh 冒頭の `LC_ALL=C` はこれを防ぐためにある。C ロケールならバイト単位の比較になり、
# 全角文字は必ずどこかのバイトで弾かれる）。CLI の drain で消される前に、hook が spool に
# 書いた直後の状態を直接見て、hook 側の判定だけを検査する。
feed_message "Ａｂｃ" 0 true "全角の message_id です。"
node -e '
  const fs = require("fs");
  const ok = !fs.existsSync(process.argv[1]);
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  全角英字の message_id は chatter_safe_name（LC_ALL=C 依存の文字クラスマッチ）で弾かれている${ok ? "" : " (spool にファイルが書かれた)"}`);
  process.exit(ok ? 0 : 1);
' "$SPOOL/Ａｂｃ.0.json"

# 無効化されている間は何も積まない（hook 側）。`delta`（feed_message + CLI起動）を使うことで、
# CLI 側にも同じ env が渡り、isSpeakDisabled（core/src/core/config.ts）が実際に呼ばれる状態で
# 確認する（R3: CLI を呼ばない feed_message だけだと CLI 側の判定は一度も通らない）。
CHATTER_AGENT_DISABLE=1 delta m-off 0 true "無効化中の発言です。"
# ★ =0 は「無効化の解除」なので積まれる。presence 判定に戻すとここが落ちる（#4）
CHATTER_AGENT_DISABLE=0 feed_message m-ddd 1 false "ゼロは解除です。"

# ★ R3: ここまでの CHATTER_AGENT_DISABLE=0 は hook 側の判定しか通っていない
#   （直後の CLI 起動は `delta m-ddd 2 true ""` で、env なしの別コマンドとして実行される）。
#   isSpeakDisabled を旧来の presence 判定（`if (process.env.CHATTER_AGENT_DISABLE) return;`）に
#   戻しても、ここまでの検証は全部グリーンのままになる（"0" は非空文字列なので presence 判定でも
#   truthy になり、"1" のケースと結果が区別できない）。CLI に**直接** CHATTER_AGENT_DISABLE=0 を
#   渡して起動し、既に spool にある delta が実際に発話されることを見て初めて、CLI 側の
#   parseBoolean("0") === false（＝無効化しない）という判定を通せる。
#
# ★ ここだけは判定結果を**その場で**確認する（結果の検証セクションまで待たない）。CLI は
#   毎回再起動されるだけの短命プロセスなので、この呼び出しが誤って何もしなくても、後続の
#   `delta m-ddd 2 true ""` が同じ spool を拾って結局発話してしまい、最終状態だけを見る限り
#   presence 判定への回帰が隠れてしまう（実際に踏んで気づいた）。
feed_message m-cli-zero 0 true "CLIにも0を渡して発話される確認用の発言です。"
CHATTER_AGENT_DISABLE=0 node "$CLI"
node -e '
  const fs = require("fs");
  let ok = false;
  try {
    ok = fs.readFileSync(process.argv[1], "utf8").includes("CLIにも0を渡して発話される確認用の発言です。");
  } catch {}
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  CLI に直接 CHATTER_AGENT_DISABLE=0 を渡すと、その場で発話される（presence 判定への回帰検出 / R3）`);
  process.exit(ok ? 0 : 1);
' "$LOG"

# final:true の delta は、メッセージが改行で終わると空で届く。空でも保留中の最終文が出ること
delta m-ddd 2 true ""
spoken

show "⑧ 同一メッセージに30並行で hook を起動しても、ファイルが壊れず index も欠けない"

# ★ 追記をやめて tmp + rename（1 delta 1 ファイル）にしたので、複数 hook が同時に走っても
#   互いの書き込みに割り込む余地が構造的に無い（旧実装は同じ .jsonl への追記だったため、
#   printf の 1024 バイト境界で他の hook の write に割り込まれて UTF-8 の途中で千切れることが
#   実測されていた）。ここでは「並行に起動しても取りこぼし・破損が起きない」ことを確認する。
LONG=$(node -e 'process.stdout.write("あ".repeat(700))')
for i in $(seq 0 29); do
  feed_message m-race "$i" false "$LONG" &
done
wait
node -e '
  const fs = require("fs");
  const dir = process.argv[1];
  const files = fs.readdirSync(dir).filter((f) => f.startsWith("m-race."));
  const indices = [];
  let broken = 0;
  for (const f of files) {
    try {
      const payload = JSON.parse(fs.readFileSync(`${dir}/${f}`, "utf8"));
      indices.push(payload.index);
    } catch {
      broken++;
    }
  }
  const missing = [];
  for (let i = 0; i < 30; i++) if (!indices.includes(i)) missing.push(i);
  console.log(`  ${files.length} ファイル中 壊れたファイル ${broken}、欠番 ${missing.length}`);
  if (files.length !== 30 || broken > 0 || missing.length > 0) {
    console.error("並行起動でファイルが壊れた/欠けた。plugin/scripts/on-message.sh の tmp + rename を確認すること");
    process.exit(1);
  }
' "$SPOOL"
rm -f "$SPOOL"/m-race.*.json

show "⑨ 無効化中でも stdin を読み切る（EPIPE 対策 / R4）"

# ★ CHATTER_AGENT_DISABLE=1 の判定が chatter_read_payload より先だと、stdin を読み切る前に
#   exit してしまい、書き込み側（Claude Code）が EPIPE になる
#   （"Hook command closed stdin before hook input was fully written"）。
#   実測: 300KB の payload で確定的に EPIPE、小さい payload でも書き込みに遅延があると 30回中16回。
#   ExitPlanMode / AskUserQuestion は tool_input に計画全文を含むので、64KB のパイプバッファを
#   普通に超える。プラグインをミュートしたユーザーが、沈黙ではなく delta ごとにエラーを
#   受け取る状態になっていた。
# on-message.sh と on-prompt.sh の両方で確認する（どちらも同じ順序ミスを踏んでいた）。
# ExitPlanMode / AskUserQuestion は tool_input に計画全文を含むので、on-prompt.sh 用の
# payload も同じく300KB相当の tool_input で再現する。
node -e '
  const { spawn } = require("child_process");
  const [onMessagePath, onPromptPath] = process.argv.slice(1);
  const big = "x".repeat(300 * 1024);
  const cases = [
    {
      label: "on-message.sh",
      hookPath: onMessagePath,
      payload: {
        session_id: "sess-1", transcript_path: "/tmp/x.jsonl", cwd: "/tmp", prompt_id: "p1",
        hook_event_name: "MessageDisplay", turn_id: "turn-1",
        message_id: "m-epipe", index: 0, final: true, delta: big,
      },
    },
    {
      label: "on-prompt.sh",
      hookPath: onPromptPath,
      payload: {
        session_id: "sess-1", prompt_id: "p-epipe", hook_event_name: "PreToolUse",
        tool_name: "ExitPlanMode", tool_input: { plan: big },
      },
    },
  ];

  let remaining = cases.length;
  let failed = false;
  for (const { label, hookPath, payload } of cases) {
    const env = { ...process.env, CHATTER_AGENT_DISABLE: "1" };
    const p = spawn(hookPath, { env, stdio: ["pipe", "ignore", "ignore"] });
    let err = null;
    p.stdin.on("error", (e) => { err = e.code; });
    p.on("close", () => {
      const ok = err === null;
      if (!ok) failed = true;
      console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  CHATTER_AGENT_DISABLE=1 中に300KB相当のpayloadを ${label} に流してもEPIPEにならない${ok ? "" : ` (${err})`}`);
      remaining--;
      if (remaining === 0) process.exit(failed ? 1 : 0);
    });
    p.stdin.end(JSON.stringify(payload));
  }
' "$ON_MESSAGE" "$ON_PROMPT"
rm -f "$SPOOL"/m-epipe.*

show "⑩ トップレベルキー探索が payload 全体に対して二乗にならない（R5）"

# ★ `${var#*pat}` は "マッチしない" とき bash 3.2 で二乗になる。将来 Claude Code が
#   message_id を改名・省略するリスクを模して、64KB の payload に message_id を含めずに流す。
#   窓（CHATTER_HEAD_WINDOW）を切っていないと、chatter_json_string の2段目
#   （整形済み JSON フォールバック）まで含めて非マッチ走査を2回payする（実測 4.2秒）。
#   閾値は `MessageDisplay` の 10秒 timeout に十分な余裕を見て 1秒とする。
node -e '
  const { spawnSync } = require("child_process");
  const big = "x".repeat(65536);
  const payload = JSON.stringify({
    session_id: "sess-1", transcript_path: "/tmp/x.jsonl", cwd: "/tmp", prompt_id: "p1",
    hook_event_name: "MessageDisplay", turn_id: "turn-1",
    index: 0, final: false, delta: big,
  });
  const threshold = 1000;
  const t0 = Date.now();
  spawnSync(process.argv[1], { input: payload, stdio: ["pipe", "ignore", "ignore"] });
  const elapsed = Date.now() - t0;
  const ok = elapsed <= threshold;
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  message_id が無い64KB payloadの処理が ${elapsed}ms（閾値 ${threshold}ms）`);
  process.exit(ok ? 0 : 1);
' "$ON_MESSAGE"

show "⑪ 無効化フラグの trim を bash と Node で揃える（R6）"

# ★ 全角スペース（U+3000）+ "1"。chatter_disabled が ASCII の [[:space:]] だけで trim すると
#   "　1" は "1" と一致せず「無効化されない」と判定される。Node 側の .trim()（parseBoolean）は
#   Unicode 対応なので同じ値を正しく無効化と判定する — 両側が食い違うと、hook は spool に
#   積み続けるのに CLI は毎回 return して何もドレインしない、という診断の出ない詰みになる
#   （日本語プロジェクトの IME コピペで容易に起きる）。
#
# ★ その場で確認する。`delta` の中の CLI 呼び出しが誤ってドレインしなくても、後続の
#   CLI 呼び出しが同じ spool を拾って結局発話してしまい、hook 側が誤って書いてしまった事実が
#   隠れる（R3 で踏んだのと同じ罠）。ここで見たいのは「hook がそもそも書かなかったか」なので、
#   spool ファイルの有無を直接見る。
IDEO_SPACE=$(printf '\xe3\x80\x80')
CHATTER_AGENT_DISABLE="${IDEO_SPACE}1" delta m-fullwidth 0 true "全角スペース付きで無効化されるはずの発言です。"
node -e '
  const fs = require("fs");
  const [logPath, spoolFile] = process.argv.slice(1);
  let logged = false;
  try { logged = fs.readFileSync(logPath, "utf8").includes("全角スペース付きで無効化されるはずの発言です。"); } catch {}
  const wroteToSpool = fs.existsSync(spoolFile);
  const ok = !logged && !wroteToSpool;
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  全角スペース付きの CHATTER_AGENT_DISABLE で bash 側と Node 側の判定が揃う${ok ? "" : ` (logged=${logged} wroteToSpool=${wroteToSpool})`}`);
  process.exit(ok ? 0 : 1);
' "$LOG" "$SPOOL/m-fullwidth.0.json"
rm -f "$SPOOL"/m-fullwidth.*

# ★ NBSP（U+00A0）+ "1"。全角スペースとは別のバイト列（C2 A0）なので、chatter_disabled が
#   全角スペースだけを個別対応して NBSP を見落とす実装でもこのケースだけは通り得る。
#   ブラウザ/エディタからのコピペで地味に混入する空白なので、別ケースとして独立に確認する。
NBSP=$(printf '\xc2\xa0')
CHATTER_AGENT_DISABLE="${NBSP}1" delta m-nbsp 0 true "NBSP付きで無効化されるはずの発言です。"
node -e '
  const fs = require("fs");
  const [logPath, spoolFile] = process.argv.slice(1);
  let logged = false;
  try { logged = fs.readFileSync(logPath, "utf8").includes("NBSP付きで無効化されるはずの発言です。"); } catch {}
  const wroteToSpool = fs.existsSync(spoolFile);
  const ok = !logged && !wroteToSpool;
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  NBSP付きの CHATTER_AGENT_DISABLE で bash 側と Node 側の判定が揃う${ok ? "" : ` (logged=${logged} wroteToSpool=${wroteToSpool})`}`);
  process.exit(ok ? 0 : 1);
' "$LOG" "$SPOOL/m-nbsp.0.json"
rm -f "$SPOOL"/m-nbsp.*

show "⑫ node / CLI 不在を診断できる（R7）"

# ★ CHATTER_AGENT_CLI は検証全体でわざと存在しないパスを指している（このスクリプト冒頭で
#   デタッチ起動を抑止するために設定）。つまり chatter_spawn_cli の「CLI が無い」早期 return は
#   検証中ずっと通っているのに、これまで一度も診断ログの中身を検査していなかった。
rm -f "$RUNTIME/hook-debug.log"

PAYLOAD_CLI_MISSING=$(node -e '
  console.log(JSON.stringify({
    session_id: "sess-1", transcript_path: "/tmp/x.jsonl", cwd: "/tmp", prompt_id: "p1",
    hook_event_name: "MessageDisplay", turn_id: "turn-1",
    message_id: "m-r7-cli", index: 0, final: true, delta: "CLI不在の診断確認用です。",
  }));
')
printf '%s' "$PAYLOAD_CLI_MISSING" | CHATTER_AGENT_HOOK_DEBUG=1 "$ON_MESSAGE"

# ★ node が PATH に無いケースは、`node` を解決できるディレクトリを PATH から全部外して
#   再現する（PATH を丸ごと空にすると mv / perl / date まで壊れて別の失敗と混ざる）。
#   mise は実体のディレクトリと shims ディレクトリの両方を PATH に通すので、実体だけ除いても
#   shims 経由で解決されてしまう（実際に踏んだ）。「`node` という実行可能ファイルを持つ
#   ディレクトリを1つでも通す」と再現できないので、両方とも除く。
#   mise で固定した Node が、対話 rc を経由しない起動（Finder / Dock から起動した Claude Code）
#   では PATH に載らないのと同じ状況を再現している。
FILTERED_PATH=
OLD_IFS=$IFS
IFS=:
for dir in $PATH; do
  [ -x "$dir/node" ] && continue
  FILTERED_PATH="${FILTERED_PATH:+$FILTERED_PATH:}$dir"
done
IFS=$OLD_IFS
PAYLOAD_NODE_MISSING=$(node -e '
  console.log(JSON.stringify({
    session_id: "sess-1", transcript_path: "/tmp/x.jsonl", cwd: "/tmp", prompt_id: "p1",
    hook_event_name: "MessageDisplay", turn_id: "turn-1",
    message_id: "m-r7-node", index: 0, final: true, delta: "node不在の診断確認用です。",
  }));
')
# ★ CHATTER_AGENT_CLI はスクリプト全体でわざと存在しないパスに向けてある（デタッチ起動の抑止）
#   ので、そのままだと「CLI が無い」の分岐が先に成立して「node が無い」まで届かない。
#   この1呼び出しだけ実在するバンドル済み CLI に向け直し、node 側の分岐を単独で踏ませる。
printf '%s' "$PAYLOAD_NODE_MISSING" | CHATTER_AGENT_HOOK_DEBUG=1 CHATTER_AGENT_CLI="$CLI" PATH="$FILTERED_PATH" "$ON_MESSAGE" 2>/dev/null

node -e '
  const fs = require("fs");
  let log = "";
  try { log = fs.readFileSync(process.argv[1], "utf8"); } catch {}
  const hasCliMissing = log.includes("SpawnCli") && log.includes("cli not found");
  const hasNodeMissing = log.includes("SpawnCli") && log.includes("node not found");
  const check = (label, ok) => { console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  ${label}`); return ok; };
  const ok1 = check("CLI 不在が診断ログに残る（chatter_spawn_cli）", hasCliMissing);
  const ok2 = check("node 不在が診断ログに残る（chatter_spawn_cli）", hasNodeMissing);
  process.exit(ok1 && ok2 ? 0 : 1);
' "$RUNTIME/hook-debug.log"
rm -f "$SPOOL"/m-r7-cli.* "$SPOOL"/m-r7-node.*

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

  // ⑦ hook 側のガード。ここが崩れると spool の形から壊れるので、記録の中身で裏を取る
  const spoolFiles = fs.readdirSync(process.argv[2]);
  // 本文の "WRONG-ID" は読み上げられて正しい（アシスタントがそう喋ったのだから）。
  // 見るべきは**どのメッセージに属したか**で、貪欲マッチだとファイルが WRONG-ID.<index>.json に割れる
  check("delta 本文の \"message_id\" に引っ張られていない（貪欲マッチの回帰）",
    !rows.some((r) => r.messageId === "WRONG-ID") &&
    !spoolFiles.some((f) => f.startsWith("WRONG-ID")) &&
    rows.filter((r) => r.text.includes("WRONG-ID")).every((r) => r.messageId === "m-ddd"));
  check("サブエージェント（agent_id あり）の発言は読み上げていない",
    !texts.some((t) => t.includes("サブエージェントの発言")) && !spoolFiles.some((f) => f.startsWith("m-sub")));
  check("message_id が取れない payload は spool に積まれていない",
    !texts.some((t) => t.includes("message_id なし")));
  check("ファイル名に使えない message_id は弾かれている（パストラバーサル）",
    !texts.some((t) => t.includes("パストラバーサル")) && !fs.existsSync(`${process.argv[2]}/../escape.0.json`));
  check("CHATTER_AGENT_DISABLE=1 の間は何も積まれていない",
    !texts.some((t) => t.includes("無効化中の発言")) && !spoolFiles.some((f) => f.startsWith("m-off")));
  check("CHATTER_AGENT_DISABLE=0 では黙らない（#4 / presence 判定への逆戻り検出）",
    texts.includes("ゼロは解除です。"));
  // ★ R3 の「CLI に直接 =0 を渡す」チェックはここではなく、その場（⑦ 内）で見ている。
  //   最終状態だけを見ると、後続の CLI 呼び出しが同じ spool を拾って結局発話してしまい、
  //   presence 判定への回帰が隠れてしまうため（上のコメント参照）
  check("CLI に直接 CHATTER_AGENT_DISABLE=0 を渡した発言も、最終的な記録に残っている",
    texts.includes("CLIにも0を渡して発話される確認用の発言です。"));
  check("final:true の delta が空でも、保留していた最終文が出る",
    texts.includes("次に進みます。"));

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
