#!/bin/bash
# MessageDisplay hook。assistant の delta を spool に1ファイルとして置き、CLI を起こす。
#
# ここは「捕捉」だけを担当する。delta の結合・Markdown 除去・文分割・感情判定・seq 採番は
# すべて chatter-agent-speak（CLI）側でやる。この分離が `MessageDisplay` の 10 秒タイムアウトと
# UI をブロックしうるリスクの両方を、構造で回避している。→ docs/plugin.md
#
# ★ 何があっても exit 0 で終える。hook の失敗が Claude Code の表示を止めてはいけない。
#
# payload はそのまま1ファイルの中身として置く。core/src/cli/spool.ts が要求する
# index / delta / final / session_id / turn_id / message_id は元から全部入っているので、
# bash 側で組み直す必要はない（組み直すと `final` を文字列にする等の事故が起きる）。
#
# ★ delta ごとに別ファイルにして tmp + rename で置く（追記はしない）。**bash から任意長の
#   追記を原子的にする移植可能な方法は無い**ため（`_lib.sh` の `chatter_write_atomic` 参照）。
#   1 delta 1 ファイルにして rename(2) の原子性に乗ることで、複数 hook の同時書き込みが
#   競合する余地そのものを構造から消している。

# shellcheck source=./_lib.sh
. "${BASH_SOURCE[0]%/*}/_lib.sh"

PLUGIN_ROOT=${BASH_SOURCE[0]%/*}/..

# ★ 順序が重要。stdin を読み切る前に exit すると Claude Code 側が EPIPE になる
#   （`_lib.sh` の chatter_read_payload 参照）。無効化判定は読み切った後に置くこと。
chatter_read_payload || exit 0

chatter_disabled && exit 0

chatter_debug MessageDisplay "$CHATTER_PAYLOAD"

# サブエージェントの発言は読み上げない。複数のサブエージェントが並列に喋ると発話が混線し、
# メインスレッドの発言との順序も意味を成さなくなる。
# `agent_id` は「サブエージェント内で発火したときだけ」入る（Claude Code のスキーマ記述）。
chatter_has_key agent_id && exit 0

# ★ ファイル名は message_id と index で決まる（`<message_id>.<index>.json`）。core は
#   ファイル名から message_id を取り出し、同じ message_id のファイルを1エントリにまとめる
#   （spool.ts）。区切りに `.` を使ってよいのは、直前の chatter_safe_name が
#   `[A-Za-z0-9_-]` 以外を弾いていて message_id に `.` が絶対に入らないから。
#   ここのサニタイズを緩めると、このファイル名のパースが壊れる。
chatter_json_string message_id || exit 0
MESSAGE_ID=$CHATTER_VALUE
chatter_safe_name "$MESSAGE_ID" || exit 0

# index はファイル名の一部になる。数値として取れない（欠落・負数など）payload は捨てる。
chatter_json_number index || exit 0
INDEX=$CHATTER_VALUE

chatter_ensure_spool || exit 0

# ★ delta が空でも書くこと。`final:true` の delta は「メッセージが改行で終わったとき空になる」
#   （Claude Code のスキーマ記述）。空だからと捨てると、final:true の payload そのものが
#   spool に残らない。[#30] 以降は final を待ってメッセージ全文をまとめて発話する設計なので、
#   被害は「最後の1文が出ない」ではなく**そのメッセージが一文も発話されない**こと
#   （同一セッションで後続イベントが起きれば救済経路が手前の deltas だけは publish するが、
#   保証ではない。起きなければ孤児掃除＝既定6時間まで発話されないまま残って消える）。
chatter_write_atomic "$CHATTER_SPOOL_DIR/$MESSAGE_ID.$INDEX.json" "$CHATTER_PAYLOAD"

chatter_spawn_cli "$PLUGIN_ROOT"

exit 0
