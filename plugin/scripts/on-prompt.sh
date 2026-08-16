#!/bin/bash
# PreToolUse(AskUserQuestion|ExitPlanMode) / Notification(permission_prompt) hook。
# 応答待ちで止まったことを、ユーザーが答える**前に**知らせる。
#
# 1イベントで完結するので `spool/prompt-<…>.json` として単発で置く。
# 整形（質問文 + 選択肢の label / 計画の見出し / 許可待ちの定型文）は
# core/src/prompt/promptEventFormatter.ts の担当。ここは payload をそのまま渡すだけ。
#
# ★ 何があっても exit 0 で終える。hook の失敗がツール実行を止めてはいけない。

# shellcheck source=./_lib.sh
. "${BASH_SOURCE[0]%/*}/_lib.sh"

PLUGIN_ROOT=${BASH_SOURCE[0]%/*}/..

# ★ 順序が重要。stdin を読み切る前に exit すると Claude Code 側が EPIPE になる
#   （`_lib.sh` の chatter_read_payload 参照）。無効化判定は読み切った後に置くこと。
chatter_read_payload || exit 0

chatter_disabled && exit 0

chatter_debug PromptEvent "$CHATTER_PAYLOAD"

# ★ ここでは agent_id を見ない。サブエージェントの「発言」は読み上げないが（on-message.sh）、
#   許可待ちはユーザーが答えるまで進まないので、誰が要求したかに関わらず知らせる価値がある。

# ★ payload をそのまま置くこと。core は `hook_event_name` / `tool_name` / `tool_input` /
#   `message` / `session_id` に加えて **`prompt_id` を必要とする**。PreToolUse と直後の
#   Notification を 10 秒窓でペア判定して重複を消しており、`prompt_id` を落とすと
#   AskUserQuestion の直後に "Claude needs your permission" が二重に読み上げられる。

chatter_ensure_spool || exit 0

# 同時に2つ来ても潰れない一意な名前。順序はファイル名ではなく birthtime で決まるので、
# 名前に求められるのは一意性だけ（時刻は spool を覗いたときに読めるように入れてある）。
NAME=prompt-$(date +%s)-$$-$RANDOM

# tmp + rename で置く（chatter_write_atomic / _lib.sh）。ワーカーは読めない payload を
# 消さずに次のドレインへ回すので書きかけを掴んでも失われないが、余計な往復が減る。
# ★ tmp は `*.json.tmp` になる。`*.tmp.json` だと prompt として拾われる（spool.ts）。
chatter_write_atomic "$CHATTER_SPOOL_DIR/$NAME.json" "$CHATTER_PAYLOAD"

chatter_spawn_cli "$PLUGIN_ROOT"

exit 0
