#!/bin/bash
# MessageDisplay hook。assistant の delta を spool に積んで CLI を起こす。
#
# ここは「捕捉」だけを担当する。delta の結合・Markdown 除去・文分割・感情判定・seq 採番は
# すべて chatter-agent-speak（CLI）側でやる。この分離が `MessageDisplay` の 10 秒タイムアウトと
# UI をブロックしうるリスクの両方を、構造で回避している。→ docs/plugin.md
#
# ★ 何があっても exit 0 で終える。hook の失敗が Claude Code の表示を止めてはいけない。
#
# payload はそのまま1行として追記する。core/src/cli/spool.ts が要求する
# index / delta / final / session_id / turn_id / message_id は元から全部入っているので、
# bash 側で組み直す必要はない（組み直すと `final` を文字列にする等の事故が起きる）。

# shellcheck source=./_lib.sh
. "${BASH_SOURCE[0]%/*}/_lib.sh"

PLUGIN_ROOT=${BASH_SOURCE[0]%/*}/..

chatter_disabled && exit 0

chatter_read_payload || exit 0

chatter_debug MessageDisplay "$CHATTER_PAYLOAD"

# サブエージェントの発言は読み上げない。複数のサブエージェントが並列に喋ると発話が混線し、
# メインスレッドの発言との順序も意味を成さなくなる。
# `agent_id` は「サブエージェント内で発火したときだけ」入る（Claude Code のスキーマ記述）。
chatter_has_key agent_id && exit 0

# ★ ファイル名は message_id だけで決まる。core は中身の message_id ではなく
#   **ファイル名から `.jsonl` を落としたもの**を messageId として使う（spool.ts）。
chatter_json_string message_id || exit 0
MESSAGE_ID=$CHATTER_VALUE
chatter_safe_name "$MESSAGE_ID" || exit 0

chatter_ensure_spool || exit 0

# ★ delta が空でも書くこと。`final:true` の delta は「メッセージが改行で終わったとき空になる」
#   （Claude Code のスキーマ記述）。空だからと捨てると、ファイルの削除も
#   最後の1文の flush も駆動されなくなる。
printf '%s\n' "$CHATTER_PAYLOAD" >>"$CHATTER_SPOOL_DIR/$MESSAGE_ID.jsonl" 2>/dev/null

chatter_spawn_cli "$PLUGIN_ROOT"

exit 0
