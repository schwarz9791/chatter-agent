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

# ★ chatter_safe_name のバイト単位マッチに必要。**外さないこと。**
#
#   `case "$1" in *[!A-Za-z0-9_-]*)` は ASCII 以外を弾く意図で書いてあるが、UTF-8 ロケールでは
#   全角英数（`Ａ` `ａ` `１` 等）がこのパターンマッチで `[A-Za-z0-9_-]` に**マッチしてしまう**
#   （ロケールの文字クラスがマルチバイト文字を「英数字相当」として扱うため）。C ロケールなら
#   1バイトずつ比較するので、全角文字は必ずどこかのバイトが `[!A-Za-z0-9_-]` に当たって弾かれる。
#   `message_id` はファイル名になる（`<message_id>.<index>.json`）ので、ここが最後の砦になる。
#
#   ★ かつては「追記を1回の write(2) に収めるため」とここに書いていたが、**それは誤り**。
#   bash から任意長の追記を原子的にする移植可能な方法は無い（`printf` は stdio が 1024 バイト
#   境界で write を分割し、`LC_ALL=C` にしてもマルチバイトの分割確率が下がるだけで境界そのものは
#   消えない。`cat` 経由は macOS では原子的だが GNU cat は `st_blksize` 単位で書くので Linux で
#   割れる）。だから追記そのものをやめ、1 delta を tmp + rename で1ファイルとして置く形に変えた
#   （→ `chatter_write_atomic` / docs/plugin.md）。この節はもう追記の原子性とは無関係。
#
#   ★ export しない。ただし「export しなければ子プロセスに伝播しない」わけではない —
#   呼び出し元のシェルが既に `LC_ALL` を export 済みなら、export 属性ごと引き継がれて
#   デタッチした node にまで伝播する（実測済み）。ここでの `LC_ALL=C` は「bash 自身のこの
#   プロセスの中だけは確実に C ロケールにする」ためのもので、伝播を止める効果は期待しないこと。
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
#
# ★ trim も parseBoolean（JS の `.trim()`）と揃える。`[[:space:]]`（`LC_ALL=C` 下では ASCII の
#   空白しか含まない）だけで trim すると、全角スペース（U+3000）・NBSP（U+00A0）・BOM（U+FEFF）を
#   落とせない。これらは日本語プロジェクトの IME コピペで容易に付く。
#   `CHATTER_AGENT_DISABLE=$'　'"1"` のような値は、bash 側は空白を落とし切れず
#   `"　1"` のまま `case` に落ちて**無効化されない**のに、Node 側の `.trim()` は Unicode
#   空白として落として `"1"` を得て**無効化される**——両側の判定が食い違う。
#   このとき hook は spool に積み続けるが、CLI はロックを取る前に return するので、
#   **何も発話されず何もドレインされず、孤児掃除（既定6時間）まで spool が増え続ける**。
#   両側ともエラーを出さないので気づけない。
#
#   `LC_ALL=C` 下ではマルチバイト文字はデコードされずバイト列として扱われるので、
#   U+3000 (`E3 80 80`) / U+00A0 (`C2 A0`) / U+FEFF (`EF BB BF`) のバイト列を個別に
#   前後から剥がす。ASCII 空白との組み合わせ・繰り返し（`"　 1　"` 等）にも対応するため、
#   変化が無くなるまでループする（fork は増やさない — 全部 bash 組み込みの範囲で済む）。
chatter_disabled() {
  local v=$CHATTER_AGENT_DISABLE
  local nbsp=$'\xc2\xa0' # NBSP (U+00A0)
  local ideo=$'\xe3\x80\x80' # 全角スペース (U+3000)
  local bom=$'\xef\xbb\xbf' # BOM / ZERO WIDTH NO-BREAK SPACE (U+FEFF)
  local prev
  while :; do
    prev=$v
    v=${v#"${v%%[![:space:]]*}"} # 先頭の ASCII 空白を落とす
    v=${v%"${v##*[![:space:]]}"} # 末尾の ASCII 空白を落とす
    case $v in
      "$nbsp"*) v=${v#"$nbsp"} ;;
      "$ideo"*) v=${v#"$ideo"} ;;
      "$bom"*) v=${v#"$bom"} ;;
    esac
    case $v in
      *"$nbsp") v=${v%"$nbsp"} ;;
      *"$ideo") v=${v%"$ideo"} ;;
      *"$bom") v=${v%"$bom"} ;;
    esac
    [ "$v" = "$prev" ] && break
  done
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
# ★ 呼び出し側は **`chatter_disabled` より先にこれを呼ぶこと。** `chatter_disabled` の
#   判定結果で早期 exit したくなるが、その exit の前に stdin を読み切っていないと、
#   書き込み側（Claude Code）が EPIPE になる。実測: `CHATTER_AGENT_DISABLE=1` + 300KB の
#   payload で確定的に EPIPE、小さい payload でも書き込みに遅延があると 30回中16回。
#   `ExitPlanMode` / `AskUserQuestion` は `tool_input` に計画全文を含むので、64KB のパイプ
#   バッファを普通に超える。プラグインをミュートしたユーザーが、沈黙ではなく delta ごとに
#   エラーを受け取る状態になる。
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

# chatter_json_string / chatter_json_number の走査窓（バイト数）。
#
# ★ `${var#*pat}` は**マッチしないとき** bash 3.2 で二乗になる（実測 LC_ALL=C・非マッチ時の
#   1回の strip）: 4KB 16ms → 16KB 141ms → 64KB **2.1秒**。`MessageDisplay` の timeout は
#   10秒しかない。しかも `chatter_json_string` の整形済み JSON フォールバック（`": "` 区切り）は、
#   実運用のコンパクト JSON では**構造上ヒットしない**ので、最初の非マッチ走査の後にもう一段
#   非マッチ走査を重ねるだけになる。`message_id` が改名・省略された payload（64KB・delta 込み）を
#   実際に流すと、非マッチ×2 で **4.2秒**（実測）。将来 Claude Code が `message_id` を改名・省略
#   したら、**全 delta が毎回これを払って timeout に近づく**。
#
#   対策は探索対象を payload の先頭 CHATTER_HEAD_WINDOW バイトに限ること。`session_id` /
#   `transcript_path` / `cwd` / `prompt_id` / `hook_event_name` / `turn_id` / `message_id` /
#   `index` といったトップレベルのキーは、Claude Code が `JSON.stringify` でオブジェクトの
#   宣言順にそのまま出す以上 payload の冒頭に固まっている。4096 バイトは、実測で最悪だった
#   64KB 全文探索（2.1秒/回）の 1/16 の長さで、同じ二乗特性から逆算しても非マッチ2回でおよそ
#   数十ms に収まる一方、深いディレクトリの `transcript_path` / `cwd` を含めても十分な余裕がある。
#
#   ★ 窓を切っているのは「マッチの有無を確かめる走査」であり、**値の切り出しも同じ窓の中で
#   完結する前提**を置いている。`message_id` / `index` の値そのものは数十バイトを超えないので、
#   窓の終端をまたいで値が千切れる実害は無い（本文側の `delta` が窓の外に出るのは想定どおり）。
CHATTER_HEAD_WINDOW=4096

# payload のトップレベルから文字列値を取り出して CHATTER_VALUE に入れる。
#
# ★ sed の `.*"key"` を使わないこと。**貪欲マッチで最後の出現を拾う**ので、
#   delta 本文に同じキー名が出てくると別の値を掴む（このリポジトリでは実際に起きる）。
#   `${var#*pat}` は最短一致なので、常に最初の出現＝トップレベルの値を取る。
#
# ★ 本文への誤爆も起きない。JSON の文字列の中では " が必ず \" にエスケープされるため、
#   `"key":"` という並びは本文側には現れない。
#
# ★ 走査対象は payload 全体ではなく先頭 CHATTER_HEAD_WINDOW バイト（`head`）に限る。
#   理由は上の CHATTER_HEAD_WINDOW のコメント参照。
chatter_json_string() { # key
  local head=${CHATTER_PAYLOAD:0:$CHATTER_HEAD_WINDOW}
  local rest=${head#*\"$1\":\"}
  if [ "$rest" = "$head" ]; then
    # 整形済み JSON（`": "` 区切り）で届いた場合
    rest=${head#*\"$1\": \"}
    if [ "$rest" = "$head" ]; then
      CHATTER_VALUE=
      return 1
    fi
  fi
  CHATTER_VALUE=${rest%%\"*}
  [ -n "$CHATTER_VALUE" ]
}

# payload のトップレベルから非負整数を取り出して CHATTER_VALUE に入れる。
#
# ★ chatter_json_string は文字列値（前後を `"` で囲まれた値）専用。`index` は数値なので
#   `"index":0` のように引用符が無く、chatter_json_string では取れない。
#
# `${var#*pat}` は最短一致なので常にトップレベル（最初の出現）の値を取る。JSON の文字列の
# 中では `"` が必ず `\"` にエスケープされるため、`"key":` という並びは本文側には現れない
# （chatter_json_string と同じ理屈）。数値には囲みの引用符が無い分、こちらは一段構えで済む。
#
# ★ こちらも走査対象は先頭 CHATTER_HEAD_WINDOW バイトに限る（chatter_json_string と同じ理由）。
chatter_json_number() { # key
  local head=${CHATTER_PAYLOAD:0:$CHATTER_HEAD_WINDOW}
  local rest=${head#*\"$1\":}
  if [ "$rest" = "$head" ]; then
    CHATTER_VALUE=
    return 1
  fi
  # 先頭の空白を落とす（整形済み JSON `"key": 0` に対応）
  rest=${rest#"${rest%%[![:space:]]*}"}
  case "$rest" in
    [0-9]*) ;;
    *)
      CHATTER_VALUE=
      return 1
      ;;
  esac
  # 先頭から連続する数字だけを切り出す（`,` や `}` など次の区切りの手前まで）
  CHATTER_VALUE=${rest%%[!0-9]*}
  # 数字の直後が `.` なら小数（例: "1.5"）。整数の先頭だけを掴んで通すと値を取り違えるので、
  # 非負「整数」ではないものとして捨てる
  case ${rest#"$CHATTER_VALUE"} in
    .*)
      CHATTER_VALUE=
      return 1
      ;;
  esac
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

# tmp + rename で1ファイルとして原子的に置く。rename(2) はファイルシステム内で原子的なので、
# 読み手には「書く前」か「書き終わった後」の2状態しか見えない。
#
# ★ **bash から任意長の追記を原子的にする移植可能な方法は無い。** `printf` は stdio が
#   1024 バイト境界で write を分割するため、複数の hook が同じファイルに追記すると、その境界で
#   互いの書き込みに割り込まれうる（実測: ASCII 4000B・30並行・4試行で 240 行中 8 行が破損）。
#   `LC_ALL=C` にしても分割確率が下がるだけで境界そのものは消えない。`cat` 経由なら macOS では
#   原子的だが、GNU cat は `st_blksize` 単位で書くので Linux で割れる。
#   だから spool への書き込みは**追記をやめ、1イベント1ファイルを tmp + rename で置く**形に
#   統一してある（on-message.sh の delta / on-prompt.sh の prompt イベント、どちらもここを通す）。
#
# ★ tmp 名は呼び出し側が決めた target に `.tmp` を足すだけ。spool の走査（core/src/cli/spool.ts
#   の classify）は「`.json` で終わるか」で message / prompt を判定するので、`.tmp` で終わる
#   名前はどの判定にも一致せず、rename が終わるまで自然に無視される。
chatter_write_atomic() { # target_path payload
  local target=$1
  local tmp=$target.tmp
  printf '%s\n' "$2" >"$tmp" 2>/dev/null &&
    mv "$tmp" "$target" 2>/dev/null
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
#
# ★ 2つの早期 return は診断を残すこと。spool をドレインする経路も孤児掃除（既定6時間）を
#   走らせる経路もここ以外に無いので、無言で return すると「診断情報ゼロのまま恒久的に沈黙する」
#   プラグインになる。本リポジトリは mise で Node を固定していて、shim が PATH に載るのは
#   対話 rc 経由のみ — **Finder / Dock から起動した Claude Code はその PATH を継承しない**ため、
#   `command -v node` が失敗する状況は現実に起きる。`chatter_debug` は既定 OFF のログなので、
#   ここに書いても普段のコストは増えない。
chatter_spawn_cli() { # plugin_root
  local cli=${CHATTER_AGENT_CLI:-$1/bin/chatter-agent-speak.mjs}
  if [ ! -f "$cli" ]; then
    chatter_debug SpawnCli "cli not found: $cli"
    return 0
  fi
  if ! command -v node >/dev/null 2>&1; then
    chatter_debug SpawnCli "node not found in PATH: $PATH"
    return 0
  fi
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
