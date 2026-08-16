#!/bin/bash
# on-message.sh / on-prompt.sh の共通部分。単体では実行しない。
#
# ★ ここで外部コマンドを起動しないこと。
#   `MessageDisplay` は matcher 非対応で全セッションで必ず発火し、実測 0.5〜3.5 秒ごとに
#   呼ばれる。タイムアウトは 10 秒しかない（他の hook は 600 秒）。
#   判定も抽出も bash のパラメータ展開だけで済ませてあるので、CLI を起こす直前まで fork が無い。
#   → docs/plugin.md
#
# macOS の /bin/bash は 3.2 なので、${var,,} や $EPOCHREALTIME は使えない。

# ★ 追記を1回の write(2) に収めるための指定。**外さないこと。**
#
#   マルチバイトのロケールだと bash の printf は文字単位で書き出し、**1024 バイトごとに
#   write が分かれる**。hook は並行して走りうるので（実測: PreToolUse と MessageDisplay が
#   同時に走って診断ログが割れた）、同じファイルへの追記が 1024 バイト境界で相手の書き込みに
#   割り込まれ、UTF-8 文字の途中で千切れる。
#
#   spool の .jsonl でこれが起きると、その行が JSON として読めなくなる。core は読めない行を
#   飛ばすので `index` の連番がそこで途切れ、**そのメッセージの以降の delta が丸ごと発話されない**。
#   しかも spool は final を処理できないまま孤児掃除まで残る。
#
#   payload は日本語を含むと 1024 バイトを普通に超えるので、実際に踏む。
#   C ロケールならバイト列として一括で write されるため割れない（実測: 240 並行追記で 0 件）。
#
#   export しないこと。bash 自身のロケールだけを変え、デタッチする node には伝播させない。
LC_ALL=C

# ランタイムルート。core/src/core/paths.ts と同じ規則を一行で書く。
#
# ★ 条件分岐を足さないこと。paths.ts の冒頭が
#   「`${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/spool` の一行で書ける以上のことをしない」
#   と決めている。足した瞬間に bash 側と Node 側が静かにズレる。
#   （このため Windows の %APPDATA% は hook 側では見ていない。→ docs/plugin.md）
CHATTER_RUNTIME_DIR=${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent
CHATTER_SPOOL_DIR=$CHATTER_RUNTIME_DIR/spool

# 読み取った payload（1行の JSON）。chatter_read_payload が入れる
CHATTER_PAYLOAD=
# chatter_json_string が取り出した値
CHATTER_VALUE=

# 無効化されているか（無限ループ防止の第1層。要約プロセスはこれを付けて spawn される）。
#
# ★ 判定は core/src/core/config.ts の parseBoolean と同じトークン集合に揃える。
#   「設定されていれば無効」にすると `CHATTER_AGENT_DISABLE=0` を「無効化の解除」のつもりで
#   書いた人の発話が、診断も出ないまま全部止まる。→ #4
chatter_disabled() {
  local v=$CHATTER_AGENT_DISABLE
  v=${v#"${v%%[![:space:]]*}"} # 先頭の空白を落とす
  v=${v%"${v##*[![:space:]]}"} # 末尾の空白を落とす
  case "$v" in
    1 | [Tt][Rr][Uu][Ee] | [Yy][Ee][Ss] | [Oo][Nn]) return 0 ;;
  esac
  return 1
}

# stdin の payload を1行の JSON として CHATTER_PAYLOAD に読む。
#
# ★ stdin は必ず読み切ること。途中で終了すると Claude Code 側が EPIPE になる
#   （"Hook command closed stdin before hook input was fully written"）。
#
# JSON の文字列内の改行は \n にエスケープされているので、生の改行は落として構わない。
# 整形されて届いた場合も、改行を除けば1行の JSON のままになる。
chatter_read_payload() {
  # -d '' は NUL まで読む＝EOF まで全部読む。EOF なので終了ステータスは 1 になるが、
  # 変数には読めた分が入っている。中身があるかどうかで判定する
  IFS= read -r -d '' CHATTER_PAYLOAD
  CHATTER_PAYLOAD=${CHATTER_PAYLOAD//$'\n'/}
  CHATTER_PAYLOAD=${CHATTER_PAYLOAD//$'\r'/}
  [ -n "$CHATTER_PAYLOAD" ]
}

# payload のトップレベルから文字列値を取り出して CHATTER_VALUE に入れる。
#
# ★ sed の `.*"key"` を使わないこと。**貪欲マッチで最後の出現を拾う**ので、
#   delta 本文に同じキー名が出てくると別の値を掴む（このリポジトリでは実際に起きる）。
#   `${var#*pat}` は最短一致なので、常に最初の出現＝トップレベルの値を取る。
#
# ★ 本文への誤爆も起きない。JSON の文字列の中では " が必ず \" にエスケープされるため、
#   `"key":"` という並びは本文側には現れない。
chatter_json_string() { # key
  local rest=${CHATTER_PAYLOAD#*\"$1\":\"}
  if [ "$rest" = "$CHATTER_PAYLOAD" ]; then
    # 整形済み JSON（`": "` 区切り）で届いた場合
    rest=${CHATTER_PAYLOAD#*\"$1\": \"}
    if [ "$rest" = "$CHATTER_PAYLOAD" ]; then
      CHATTER_VALUE=
      return 1
    fi
  fi
  CHATTER_VALUE=${rest%%\"*}
  [ -n "$CHATTER_VALUE" ]
}

# payload にトップレベルのキーがあるか（値の型は問わない）。
chatter_has_key() { # key
  case "$CHATTER_PAYLOAD" in
    *\"$1\":*) return 0 ;;
  esac
  return 1
}

# ファイル名として使える値か。
#
# core 側に message_id のサニタイズは無く、`path.join(spoolDir, fileName)` するだけなので、
# ここが最後の砦になる。`.` を弾いているので `..` も `*.progress.json` との衝突も同時に塞がる。
chatter_safe_name() { # value
  case "$1" in
    "" | *[!A-Za-z0-9_-]*) return 1 ;;
  esac
  [ "${#1}" -le 200 ]
}

# spool ディレクトリを用意する。CLI は spool を作らないので、書く側の責任。
chatter_ensure_spool() {
  [ -d "$CHATTER_SPOOL_DIR" ] || mkdir -p "$CHATTER_SPOOL_DIR" 2>/dev/null
  [ -d "$CHATTER_SPOOL_DIR" ]
}

# CLI をデタッチ起動する。
#
# ★ stdin / stdout / stderr を3つとも切り離すこと。hook の fd を握ったままの子が残ると、
#   Claude Code が hook のパイプの EOF を待って止まりうる。さらに `MessageDisplay` の
#   stdout は「delta の差し替え」として解釈されるので、CLI の出力が混ざると表示まで壊れる。
#
# ★ setsid は macOS に無い。nohup + & で足りる（nohup は exec で node に化けるので常駐しない）。
#
# CLI の解決経路は1本だけにする。開発時の差し替えは CHATTER_AGENT_CLI のみ。→ docs/core.md
chatter_spawn_cli() { # plugin_root
  local cli=${CHATTER_AGENT_CLI:-$1/bin/chatter-agent-speak.mjs}
  [ -f "$cli" ] || return 0
  command -v node >/dev/null 2>&1 || return 0
  nohup node "$cli" >/dev/null 2>&1 </dev/null &
}

# 診断ログ。CHATTER_AGENT_HOOK_DEBUG が有効なときだけ書く。
#
# hook は stdout / stderr を握り潰されるので、実機で「発火したか」「payload に何が入っていたか」
# を見る窓はここしかない。既定 OFF なのは、毎 delta で date を起動したくないため。
chatter_debug() { # tag payload
  case "$CHATTER_AGENT_HOOK_DEBUG" in
    1 | [Tt][Rr][Uu][Ee] | [Yy][Ee][Ss] | [Oo][Nn]) ;;
    *) return 0 ;;
  esac
  [ -d "$CHATTER_RUNTIME_DIR" ] || mkdir -p "$CHATTER_RUNTIME_DIR" 2>/dev/null
  # delta 間隔の下限を測るのでミリ秒が要る。BSD date は %N を持たず、bash 3.2 には
  # $EPOCHREALTIME も無いので perl を使う（デバッグ時だけなので fork してよい）
  local at
  at=$(perl -MTime::HiRes -e 'printf "%.3f", Time::HiRes::time()' 2>/dev/null) || at=$(date +%s)
  printf '%s\t%s\t%s\n' "$at" "$1" "$2" >>"$CHATTER_RUNTIME_DIR/hook-debug.log" 2>/dev/null
  return 0
}
