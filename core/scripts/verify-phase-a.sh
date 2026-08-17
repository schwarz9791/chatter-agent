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

# AI要約（issue #31）の受け入れ確認に使う偽の要約 CLI。claudeCli.ts が execFileSync で
# 直接 exec するので、node 経由でラップされる fake-player.mjs と違って実行ビットが要る
# （→ fake-summarizer.mjs 冒頭のコメント）。
FAKE_SUMMARIZER="$REPO/core/scripts/fake-summarizer.mjs"
if [ ! -x "$FAKE_SUMMARIZER" ]; then
  echo "偽の要約 CLI が無いか実行権限がありません: $FAKE_SUMMARIZER" >&2
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

# ★ **同一セッションの後続イベントは、まだ final の来ていないメッセージを「救済」して
#   打ち切る**（worker.ts の hasNewerInSameSession）。独立に検査したいケースは、先に final で
#   閉じてから次を始めること。順序を崩すと、検査したかったメッセージが手前で消費されて
#   検査が空振りする（新設計で最も踏みやすい罠）。

show "① final が来るまで1文も出ない"
delta m-aaa 0 false "完全に判明しました。ストリーミングでテキストが流れてきます。"
echo "[delta 0 の直後 / final はまだ来ていない]"; spoken
delta m-aaa 1 false "対照の PostToolUse も2件出たので、設定が読み込まれたことは確定です。"
echo "[delta 1 の直後]"; spoken

show "② 未閉じコードブロックを積んでも、まだ何も出ない"
delta m-aaa 2 false $'こう書きます。\n```ts\nconst secret = 1;\n'
spoken

show "③ 大きく遅れて届く final:true。ここで初めてメッセージ全文が一括で出る"
echo "[フェンスが閉じてコードは消え、①②③ の全文が1つの塊として出る]"
delta m-aaa 3 true $'const secret = 1;\n```\nこれは実装方針に関わる分岐があるので確認させてください。'
spoken

show "④ final が来なかったメッセージは、後続イベントの到着で救済される"
delta m-eee 0 false "中断された発言です。この後 final は来ません。"
echo "[m-eee はまだ出ない]"; spoken
echo "[ここで別メッセージ m-bbb が始まる → m-eee が全文出る]"
delta m-bbb 0 false "次の作業に入ります。ログを確認します。"
spoken

show "⑤ 応答待ち通知（PreToolUse に付随する同一 prompt_id の Notification は捨てる）"
echo "[この prompt の到着で m-bbb も救済される]"
prompt '{"session_id":"sess-1","prompt_id":"p9","hook_event_name":"PreToolUse","tool_name":"AskUserQuestion","tool_input":{"questions":[{"question":"次は何をしますか？","options":[{"label":"進める"},{"label":"やり直す"}]}]}}'
prompt '{"session_id":"sess-1","prompt_id":"p9","hook_event_name":"Notification","message":"Claude needs your permission"}'
spoken

show "⑥ 同じ final を複数のワーカーが同時に見ても、二重に発話しない"

# ★ **final を先に置いてから並列に叩くこと。** 新設計で最も危険なレースは
#   「2つのワーカーが同じ final を見て両方 publish する」で、メッセージ全文がまるごと
#   二度発話される。final を最後に1回だけ叩く順序（旧⑥）ではそこを踏まない。
for i in 0 1 2 3 4 5; do
  feed_message m-ccc "$i" false "並列${i}です。"
done
feed_message m-ccc 6 true "終わり。"

# ★ 引数なしの `wait` は無条件に 0 を返す（実測確認済み:
#   `bash -c 'for _ in 1 2 3; do (exit 7) & done; wait; echo $?'` → `0`）。pid を渡さないと、
#   8並列のワーカーが1つでもクラッシュして検証がそのまま緑になってしまう。
#
# ★ ただしこれで拾えるのは crash（segfault / OOM / node 起動失敗）だけ。`cli/index.ts` の
#   main() は try/catch の後に無条件で `process.exit(0)` するため、JS の例外は前景で
#   実行していても exit code には出ない（＝この修正では検出できない）。過大な期待をしないこと。
pids=()
for _ in 1 2 3 4 5 6 7 8; do
  node "$CLI" &
  pids+=($!)
done
for p in "${pids[@]}"; do wait "$p"; done
spoken

show "⑦ hook 側のガード（ここが壊れると spool の形から崩れる）"

# ★ **m-ddd だけ専用セッション（sess-ddd）に隔離すること（#22）。** `hasNewer` 救済は
#   同一セッションの後続イベントで発火するので、sess-1 に置くと後続の probe
#   （m-zero / m-cli-zero）が m-ddd を救済してしまい、`final` を経由せずに発話される。
#   そうなると「空 final を捨てる」回帰（`[ -n "$delta" ] || exit 0`）を入れても検査は
#   緑のままになる ── **実際にこの隔離を入れる前は緑だった。** 専用セッションなら、
#   m-ddd を発話させる経路は自分の `final` しかない。
DDD_SESSION='{"session_id":"sess-ddd"}'

# delta 本文に "message_id" が出てくる。sed の貪欲マッチだと WRONG-ID を掴んでファイルが割れる
delta m-ddd 0 false 'JSON の "message_id":"WRONG-ID" について説明します。空のファイナルで確定します。' "$DDD_SESSION"

# ★ final:true の delta は、メッセージが改行で終わると空で届く。空でも spool に書かないと
#   メッセージ全体が**一文も**発話されない
delta m-ddd 1 true "" "$DDD_SESSION"
spoken

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
# ★ =0 は「無効化の解除」なので積まれる。presence 判定に戻すとここが落ちる（#4）。
#   final:true で閉じた独立のメッセージにしてある（#22 — 空 final の検査と絡ませない）
CHATTER_AGENT_DISABLE=0 delta m-zero 0 true "ゼロは解除です。"

# ★ R3: 上の `CHATTER_AGENT_DISABLE=0 delta m-zero ...` は、実は hook 側だけでなく CLI 側の
#   判定にも env が届いている。bash は `VAR=val funcname` の代入を関数内で実行されるコマンドに
#   **エクスポートする**（実測確認済み: `bash -c 'f(){ env | grep -c "^PROBE=1"; }; PROBE=1 f'`
#   → `1`）ので、`delta` 内の `node "$CLI"` にも CHATTER_AGENT_DISABLE=0 は伝播している
#   （「env なしの別コマンドとして実行される」という以前のコメントは誤りだった）。
#
#   それでも isSpeakDisabled を旧来の presence 判定（`if (process.env.CHATTER_AGENT_DISABLE) return;`）
#   に戻すと、この検証は依然として見抜けない。presence 判定では "0" も非空文字列なので truthy
#   （＝無効化）と判定され、m-zero のドレインはその呼び出しの場では失敗するが、spool の
#   ファイルは消えずに残るだけ。CHATTER_AGENT_DISABLE を立てない `node "$CLI"` 呼び出しが
#   このあと（⑧以降）何度も来るので、いずれかがそれを拾ってしまい、**最終状態（結果の検証
#   ブロック）だけを見ると結局発話されていて presence 判定への回帰が隠れる**。CLI に
#   **直接** CHATTER_AGENT_DISABLE=0 を渡して起動し、その場で $LOG を確認して初めて、CLI 側の
#   parseBoolean("0") === false（＝無効化しない）という判定を独立に検証できる。
#
# ★ ここだけは判定結果を**その場で**確認する（結果の検証セクションまで待たない）。CLI は
#   毎回再起動されるだけの短命プロセスなので、この呼び出しが誤って何もしなくても、後続の
#   どこかの CLI 起動が同じ spool を拾って結局発話してしまい、最終状態だけを見る限り
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

show "⑬ 孤児カスケード: 救済されたメッセージの遅延 final が、ストリーミング中の次のメッセージを打ち切らない"

# ★ **専用セッション（sess-orphan）に隔離すること（#22 で踏んだ罠と同じ）。** sess-1 に置くと
#   このスクリプトの他の probe が `hasNewerInSameSession` を誤って成立させてしまい、
#   検査したいタイミングより先に m-orphan-b が救済されてしまう（検査が空振りする）。
ORPHAN_SESSION='{"session_id":"sess-orphan"}'

# m-orphan-a は final 未着のまま置く（まだ発話されない）
delta m-orphan-a 0 false "孤児カスケード検証Aの一文目です。" "$ORPHAN_SESSION"
echo "[m-orphan-a はまだ出ない]"; spoken

# 同一セッションで m-orphan-b が始まる → hasNewerInSameSession が成立し、m-orphan-a が
# 「final 未着のまま」救済されて spool から消える（tombstone にも記録される）。
# m-orphan-b 自身は後続が無いので救済されず、ストリーミング中のまま spool に残る。
delta m-orphan-b 0 false "孤児カスケード検証Bの一文目です。" "$ORPHAN_SESSION"
echo "[m-orphan-a が救済されて全文出る。m-orphan-b はまだ出ない]"; spoken

node -e '
  const fs = require("fs");
  const [orphanFile, streamingFile] = process.argv.slice(1);
  const ok = !fs.existsSync(orphanFile) && fs.existsSync(streamingFile);
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  m-orphan-a は救済されて spool から消え、m-orphan-b はストリーミング中のまま spool に残っている`);
  process.exit(ok ? 0 : 1);
' "$SPOOL/m-orphan-a.0.json" "$SPOOL/m-orphan-b.0.json"

# ★ 救済済み（tombstone 済み）の m-orphan-a に、遅延した final が孤児として届く。
#   index 0 は既に消えているので readMessage は index 0 から結合できず deltas は空になる
#   （この1ファイルだけでは final の中身を再構築できない）。tombstone は message_id で
#   即破棄するので、この孤児の中身に関わらず捕まる想定
delta m-orphan-a 1 true "孤児カスケード検証Aの最終行です。この文が発話されたら tombstone が効いていない。" "$ORPHAN_SESSION"

# ★ ここだけは判定結果をその場で確認する（R3 と同じ理由）。この後の呼び出しはもう
#   sess-orphan に触れないので最終的にマスクされる心配は薄いが、"m-orphan-b が打ち切られて
#   いないこと" は「spool に残っている」という事実そのものが検査対象なので、片付ける前に見る。
node -e '
  const fs = require("fs");
  const [orphanFile, streamingFile, logPath] = process.argv.slice(1);
  const orphanGone = !fs.existsSync(orphanFile);
  const streamingIntact = fs.existsSync(streamingFile);
  let logged = "";
  try { logged = fs.readFileSync(logPath, "utf8"); } catch {}
  const orphanNotSpoken = !logged.includes("孤児カスケード検証Aの最終行です");
  const streamingNotTruncated = !logged.includes("孤児カスケード検証Bの一文目です");
  const ok = orphanGone && streamingIntact && orphanNotSpoken && streamingNotTruncated;
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  孤児カスケード: 遅延 final は tombstone で即破棄され、ストリーミング中の m-orphan-b は打ち切られない${ok ? "" : ` (orphanGone=${orphanGone} streamingIntact=${streamingIntact} orphanNotSpoken=${orphanNotSpoken} streamingNotTruncated=${streamingNotTruncated})`}`);
  process.exit(ok ? 0 : 1);
' "$SPOOL/m-orphan-a.1.json" "$SPOOL/m-orphan-b.0.json" "$LOG"
spoken

# m-orphan-b は意図的にストリーミング中のまま（final を送らない）でこの節を終える。
# それ自体が検査したかった状態なので、final で閉じずに spool の残骸だけ片付ける
rm -f "$SPOOL"/m-orphan-*

# ★ ここから AI要約（issue #31）の受け入れ確認。要約は既定 OFF なので、CHATTER_AGENT_AI_SUMMARY_*
#   は `VAR=val delta ...` の形で**その1回の呼び出しにだけ**渡す（上の
#   `CHATTER_AGENT_DISABLE=0 delta m-zero ...` と同じ、R3 で確認済みの伝播規則）。
#   ここでスクリプト冒頭のように export すると、後続はもう無いとはいえ、①〜⑬の「要約は
#   既定 OFF なので結果が変わらないはず」という前提を壊しかねないので、あえてこの形を貫く。
show "⑭ AI要約: 閾値を超えたメッセージは要約されて speech.jsonl に載る（issue #31）"

# ★ aiSummaryCommand には要約 CLI の絶対パスを渡す。findCommandPath（core/src/summarizer/claudeCli.ts）
#   は絶対パスをそのまま信頼し、存在確認すらしない。差し替えられることがそのまま検証の
#   自動化になっている（playerCommand / playerArgs と同じ論法。→ docs/core.md「受け入れ確認」）。
SUMMARIZER_RECORD_1="$ROOT/summarizer-record-1.jsonl"
SUMMARIZER_REPLY_1="（要約）短くまとまりました。"

# aiSummaryThreshold の既定 200 文字を超えるよう、文ごとに内容を変えて12文つなげる。
# ★ 同じ文を繰り返すと「結果の検証」の重複チェック（重複した発話が無い）に引っかかる
LONG_TEXT=$(node -e '
  const parts = [];
  for (let i = 0; i < 12; i++) parts.push(`要約の受け入れ確認に使う長いメッセージその${i}です。`);
  process.stdout.write(parts.join(""));
')

CHATTER_AGENT_AI_SUMMARY_ENABLED=1 \
  CHATTER_AGENT_AI_SUMMARY_COMMAND="$FAKE_SUMMARIZER" \
  FAKE_SUMMARIZER_MODE=short \
  FAKE_SUMMARIZER_REPLY="$SUMMARIZER_REPLY_1" \
  FAKE_SUMMARIZER_RECORD="$SUMMARIZER_RECORD_1" \
  delta m-summary-long 0 true "$LONG_TEXT"
spoken

node -e '
  const fs = require("fs");
  const rows = fs.readFileSync(process.argv[1], "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l));
  const ok = rows.some((r) => r.text === process.argv[2]);
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  閾値を超えたメッセージが偽要約の返答に置き換わって記録されている`);
  process.exit(ok ? 0 : 1);
' "$LOG" "$SUMMARIZER_REPLY_1"

# 偽要約が受け取った --session-id と原文の長さ（200字超）を検査する。
# --session-id の値は後段⑰（無限ループ防止の第2層）で使う
node -e '
  const fs = require("fs");
  const line = fs.readFileSync(process.argv[1], "utf8").trim().split("\n")[0];
  const rec = JSON.parse(line);
  const ok = typeof rec.sessionId === "string" && rec.sessionId.length > 0 && rec.textLength > 200;
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  偽要約が --session-id と200字超の原文を受け取っている (${JSON.stringify(rec)})`);
  process.exit(ok ? 0 : 1);
' "$SUMMARIZER_RECORD_1"

# 実測ログ（summarizer.log。core/src/core/paths.ts の getSummarizerLogPath）に
# `<ISO時刻>\tok\t...` の1行が残っているはず（summaryPipeline.ts の log()）
node -e '
  const fs = require("fs");
  let lines = [];
  try { lines = fs.readFileSync(process.argv[1], "utf8").split("\n").filter(Boolean); } catch {}
  const ok = lines.length === 1 && lines[0].split("\t")[1] === "ok";
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  実測ログ（summarizer.log）に ok の判定が1行残っている${ok ? "" : ` (${JSON.stringify(lines)})`}`);
  process.exit(ok ? 0 : 1);
' "$RUNTIME/summarizer.log"

# 後段⑰（第2層）で使う session_id を取り出す
SUMMARIZER_SESSION_ID=$(node -e '
  const fs = require("fs");
  const line = fs.readFileSync(process.argv[1], "utf8").trim().split("\n")[0];
  console.log(JSON.parse(line).sessionId);
' "$SUMMARIZER_RECORD_1")

show "⑮ AI要約: 閾値以下のメッセージは要約されない（偽要約が一度も起動しない）"

# ★ 起動していないことは「記録ファイルが作られない」ことで確認する。FAKE_SUMMARIZER_RECORD は
#   偽要約プロセス自身が execFileSync された後に書くので、ファイルの有無がそのまま
#   「起動されたか」の証拠になる（summaryPipeline.ts の「2. 閾値以下 → 原文」は
#   findCommandPath より前で return するので、コマンド解決すら起きない）
SUMMARIZER_RECORD_2="$ROOT/summarizer-record-2.jsonl"
SHORT_TEXT="これは短い発言です。"

CHATTER_AGENT_AI_SUMMARY_ENABLED=1 \
  CHATTER_AGENT_AI_SUMMARY_COMMAND="$FAKE_SUMMARIZER" \
  FAKE_SUMMARIZER_RECORD="$SUMMARIZER_RECORD_2" \
  delta m-summary-short 0 true "$SHORT_TEXT"
spoken

node -e '
  const fs = require("fs");
  const [logPath, recordPath, text] = process.argv.slice(1);
  const rows = fs.readFileSync(logPath, "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l));
  const spokenOriginal = rows.some((r) => r.text === text);
  const notInvoked = !fs.existsSync(recordPath);
  const ok = spokenOriginal && notInvoked;
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  閾値以下のメッセージは原文のまま発話され、偽要約は起動していない${ok ? "" : ` (spokenOriginal=${spokenOriginal} recordExists=${!notInvoked})`}`);
  process.exit(ok ? 0 : 1);
' "$LOG" "$SUMMARIZER_RECORD_2" "$SHORT_TEXT"

show "⑯ ★ AI要約: 要約 CLI が exit 1 で失敗しても、原文がそのまま読み上げられる（発話が止まらないことがこの機能の生命線）"

# ★ ここが要約機能の生命線。summaryPipeline.ts の「5. 実行 → 失敗/タイムアウト → 原文」の
#   フォールバックが、ユニットテストだけでなく実際の hook → CLI の経路でも効くことを見る。
#   要約が失敗した「せいで」発話まで欠落したら、機能の存在自体が発話の信頼性を損なうことになる
SUMMARIZER_RECORD_3="$ROOT/summarizer-record-3.jsonl"
LONG_TEXT_FAIL=$(node -e '
  const parts = [];
  for (let i = 0; i < 10; i++) parts.push(`要約が失敗しても発話が止まらないことを確認する長い文章その${i}です。`);
  process.stdout.write(parts.join(""));
')

CHATTER_AGENT_AI_SUMMARY_ENABLED=1 \
  CHATTER_AGENT_AI_SUMMARY_COMMAND="$FAKE_SUMMARIZER" \
  FAKE_SUMMARIZER_MODE=fail \
  FAKE_SUMMARIZER_RECORD="$SUMMARIZER_RECORD_3" \
  delta m-summary-fail 0 true "$LONG_TEXT_FAIL"
spoken

node -e '
  const fs = require("fs");
  const rows = fs.readFileSync(process.argv[1], "utf8").split("\n").filter(Boolean).map((l) => JSON.parse(l));
  const ok = rows.some((r) => r.text.includes("要約が失敗しても発話が止まらないことを確認する長い文章その0です。"));
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  ★ 要約 CLI が失敗しても、原文がフォールバックとして発話される`);
  process.exit(ok ? 0 : 1);
' "$LOG"

# 「そもそも起動していないだけ」ではないことの裏取り。record ファイルは偽要約自身が
# execFileSync された後に書くので、これが在るのに発話が止まらなかった、が確認したいこと
node -e '
  const fs = require("fs");
  const ok = fs.existsSync(process.argv[1]);
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  偽要約は実際に起動された上で失敗している（no-command 等ですり抜けていない）`);
  process.exit(ok ? 0 : 1);
' "$SUMMARIZER_RECORD_3"

node -e '
  const fs = require("fs");
  let lines = [];
  try { lines = fs.readFileSync(process.argv[1], "utf8").split("\n").filter(Boolean); } catch {}
  const last = lines[lines.length - 1] ?? "";
  const ok = lines.length === 2 && last.split("\t")[1] === "error";
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  実測ログ（summarizer.log）に error の判定が追記されている${ok ? "" : ` (${JSON.stringify(lines)})`}`);
  process.exit(ok ? 0 : 1);
' "$RUNTIME/summarizer.log"

show "⑰ ★ AI要約 第2層: 要約 CLI 自身の session_id を持つ delta は発話されず spool からも消える"

# ★ 第1層（要約 CLI を CHATTER_AGENT_DISABLE=1 付きで spawn する。claudeCli.ts の runClaudeCli）
#   はユニットテストでも検証できるが、ここで見たいのは第2層（workerState.ts の
#   summarizerSessionIds）が **実際の hook → CLI の経路で**効いていること。これが今回の
#   受け入れ確認で最も価値の高いシナリオ（ユニットテストでは「実際の hook が発火し、
#   実際の CLI が spool を読んで捨てる」という経路そのものは検証できない）。
#
#   ⑭で偽要約が受け取った --session-id（＝要約 CLI に実際に渡った session_id。
#   summaryPipeline.ts の summarizeSentences が execFileSync の**前**に
#   registerSessionId → addSummarizerSession → writeWorkerState で永続化している）を、
#   そのまま次の delta の session_id として流し込む。これは「要約 CLI 自身の Claude Code が
#   MessageDisplay hook を発火させてしまった」場合のふりをしている。
SUMMARIZER_SESSION_JSON=$(node -e 'console.log(JSON.stringify({ session_id: process.argv[1] }))' "$SUMMARIZER_SESSION_ID")
SUMMARIZER_LOOP_TEXT="要約 CLI 自身が発火させた発言のふりです。"

delta m-summarizer-loop 0 true "$SUMMARIZER_LOOP_TEXT" "$SUMMARIZER_SESSION_JSON"

node -e '
  const fs = require("fs");
  const [logPath, spoolFile, text] = process.argv.slice(1);
  let logged = false;
  try { logged = fs.readFileSync(logPath, "utf8").includes(text); } catch {}
  const wroteToSpool = fs.existsSync(spoolFile);
  const ok = !logged && !wroteToSpool;
  console.log(`${ok ? "\x1b[32mPASS\x1b[0m" : "\x1b[31mFAIL\x1b[0m"}  ★ 要約 CLI に渡した session_id を持つ delta は発話されず、spool からも消える${ok ? "" : ` (logged=${logged} wroteToSpool=${wroteToSpool})`}`);
  process.exit(ok ? 0 : 1);
' "$LOG" "$SPOOL/m-summarizer-loop.0.json" "$SUMMARIZER_LOOP_TEXT"
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
  check("遅れて届いた final でメッセージの最後の文まで出ている",
    texts.includes("これは実装方針に関わる分岐があるので確認させてください。"));
  check("並列起動でも順序が保たれている",
    JSON.stringify(texts.filter((t) => t.startsWith("並列") || t === "終わり。")) ===
    JSON.stringify([...[0, 1, 2, 3, 4, 5].map((i) => `並列${i}です。`), "終わり。"]));
  check("final が来なかったメッセージが後続イベントで救済されている",
    texts.includes("中断された発言です。") && texts.includes("この後 final は来ません。"));

  // ★ ⑬ 孤児カスケード（CLAUDE.md 承認済み計画 B-3）。tombstone が無いと、救済で spool から
  //   消えたはずの m-orphan-a の遅延 final が「同一セッションの後続」として成立してしまい、
  //   まだストリーミング中の m-orphan-b を打ち切って発話してしまう
  check("孤児カスケード: 救済された m-orphan-a の一文目は発話されている",
    texts.includes("孤児カスケード検証Aの一文目です。"));
  check("孤児カスケード: 救済済みメッセージへの遅延 final（孤児）は発話されていない（tombstone）",
    !texts.some((t) => t.includes("孤児カスケード検証Aの最終行です")));
  check("孤児カスケード: ストリーミング中だった m-orphan-b は打ち切られずに発話されていない",
    !texts.some((t) => t.includes("孤児カスケード検証Bの一文目です")));

  check("spool は片付いている（処理済みのファイルが残っていない）",
    !fs.readdirSync(process.argv[2]).some((f) =>
      ["m-aaa", "m-bbb", "m-ccc", "m-ddd", "m-eee", "m-zero", "m-cli-zero", "m-orphan", "prompt-"].some((p) =>
        f.startsWith(p))));

  // ★ #30 で保証が付いた契約（docs/protocol.md「発話の粒度」）。
  //   1メッセージ分は seq 連続・ts 同値・messageId 同一の塊で配信される
  const grouped = (messageId) => rows.filter((r) => r.messageId === messageId);
  const isOneBatch = (messageId) => {
    const group = grouped(messageId);
    if (group.length === 0) return false;
    const contiguous = group.every((r, i) => i === 0 || r.seq === group[i - 1].seq + 1);
    const sameTs = new Set(group.map((r) => r.ts)).size === 1;
    // 他メッセージの発話が間に挟まっていないこと
    const firstIndex = rows.indexOf(group[0]);
    const solid = group.every((r, i) => rows[firstIndex + i] === r);
    return contiguous && sameTs && solid;
  };
  check("m-aaa の全文が1つの塊で配信されている（seq 連続 / ts 同値 / 間に他メッセージが挟まらない）",
    grouped("m-aaa").length === 5 && isOneBatch("m-aaa"));
  check("m-ccc（並列に叩いたメッセージ）も1つの塊で配信されている", isOneBatch("m-ccc"));

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
  // ★ #22: この検査の対象は、**final が来るまで一度も発話されていない**メッセージであること。
  //   m-ddd は ⑦ の先頭で開いて空 final で閉じており、その間に同一セッションの他イベントを
  //   挟んでいないので、`[ -n "$delta" ] || exit 0` の回帰でここが確定的に赤くなる
  check("final:true の delta が空でも、メッセージ全文が出る",
    texts.includes("空のファイナルで確定します。") &&
    texts.some((t) => t.includes("WRONG-ID")));

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
