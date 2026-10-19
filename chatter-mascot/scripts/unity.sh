#!/usr/bin/env bash
# Unity を batchmode で回す共通部分。
#
# ★ Editor を開いたままだと失敗する。Unity はプロジェクトを排他ロックする。
# ★ batchmode を使う理由は速さではなく、**モーダルダイアログが出ないこと**。
#   Editor 経由（MCP など）でビルドすると、保存確認ダイアログが出た瞬間に応答が返らなくなり、
#   呼び出し側からは「ハングした」としか見えない。
set -euo pipefail

PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if pgrep -f "Unity.app/Contents/MacOS/Unity.*${PROJECT_PATH}" >/dev/null 2>&1; then
  echo "The Unity Editor has this project open. Close it and run again" >&2
  exit 1
fi

if ! command -v unity >/dev/null 2>&1; then
  echo "The unity command was not found. Install it with:" >&2
  echo "  curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash" >&2
  exit 1
fi

# ★ UNITY_VERSION は CLI の --editor-version へ渡す。渡さないと CLI は
#   ProjectVersion.txt の版で走るので、**指定したつもりの版で走らない**。
#
# ★ --non-interactive を必ず付ける。CLI がプロンプトを出す状態（未ログイン、Editor が
#   未インストール）に入ると、出力は grep の裏・入力は端末のままで無言のハングに見える。
UNITY_CLI_ARGS=(--no-banner --non-interactive)
if [ -n "${UNITY_VERSION:-}" ]; then
  UNITY_CLI_ARGS+=(--editor-version "$UNITY_VERSION")
fi

# ビルド出力の grep フィルタ。ここに無いプレフィックスのログは画面に出ない
# （→ docs/knowledge/mascot-unity.md「scripts/run.sh の grep を通らないログは存在しないのと同じ」）。
UNITY_BUILD_FILTER='^Error:|^\[Build\]|^\[Native\]|^\[Icon\]|error CS|Error building|Exception|BuildFailedException|Failed to resolve|Cannot perform upm operation|Project has invalid dependencies|An error occurred while resolving packages'

# ★ 出荷物に古いライセンス表記を入れないための関門。ビルドするスクリプトが Unity を呼ぶ前に呼ぶ。
#   同梱コピーは消せない（`.app` からリポジトリの NOTICE は見えず、設定パネルは Editor からも読む）ので、
#   **コピーするのではなく一致を確かめる** —— ビルドが追跡ファイルを書き換える形にすると、
#   中断やクラッシュで食い違ったまま残る経路ができる（→ ProjectSettings/AudioManager.asset の3段構え）。
assert_notice_in_sync() {
  local source="$PROJECT_PATH/../NOTICE"
  local copy="$PROJECT_PATH/Assets/StreamingAssets/NOTICE.txt"
  if ! diff -q "$source" "$copy" >/dev/null; then
    echo "The bundled NOTICE.txt differs from the repository NOTICE. Sync it with the command below, then build again:" >&2
    echo "  cp NOTICE chatter-mascot/Assets/StreamingAssets/NOTICE.txt" >&2
    diff -u "$source" "$copy" >&2 || true
    exit 1
  fi
}

# Unity を batchmode でビルドし、grep で絞った出力・失敗時の後始末・成果物の存在確認までを行う。
#
#   unity_build_player <target> <method> <output> <log> <scene>
#
# <output> / <log> は相対でも絶対でも受ける（内側で PROJECT_PATH 基準に絶対化する）。
unity_build_player() {
  local target="$1" method="$2" output="$3" log="$4" scene="$5"

  case "$output" in
    /*) ;;
    *)  output="$PROJECT_PATH/$output" ;;
  esac
  case "$log" in
    /*) ;;
    *)  log="$PROJECT_PATH/$log" ;;
  esac

  # ★ ログは Unity を呼ぶ直前に空にする。CLI は --log-file の中身を画面へも流すので、
  #   残っていると前回のビルドの行が先に流れる。ここより手前で空にすると、Unity を
  #   起動すらしない中断（NOTICE のズレなど）が、読んでいた診断を巻き添えにする。
  mkdir -p "$(dirname "$log")"
  : > "$log"

  # ★ 古い成果物を先に消す。残っていると、失敗を 0 で返す経路が1つでもあったときに
  #   下の存在チェックまで前回の成功物で通り、直っていないバイナリを直ったつもりで起動する。
  /bin/rm -rf "$output"

  # ★ --args はシェル分割される。単一引用符で括らないと、空白を含むシーンパスが途中で切れ、
  #   空文字なら -buildScene が値なしの末尾になって BuildScript が既定シーンへ落ちる
  #   （＝頼んでいないシーンをビルドして成功を報告する）。
  local quoted=${scene//\'/\'\\\'\'}   # ' 自身は '\'' に置き換える

  # ★ --target を明示する。アクティブなビルドターゲットが別のまま残っていると、
  #   指定しないと切り替えと再インポートを待つ。
  # ★ -o は -buildOutput としてそのまま渡り、相対パスを解くのは BuildScript 側
  #   （プロジェクトルート基準）。ここで絶対化してあるので解決の場所には寄りかからない。
  # ★ ^Error: は CLI 自身の失敗（Editor が未インストール、target 不正、認証切れ）。
  #   Editor が起動する前に弾かれるので --log-file には何も残らず、ここで拾わないと
  #   終了コードだけが残って理由が画面から消える。
  set +e
  unity build "$PROJECT_PATH" --target "$target" "${UNITY_CLI_ARGS[@]}" \
    --execute-method "$method" \
    -o "$output" \
    --args "-buildScene '$quoted'" \
    --log-file "$log" --no-provenance \
    2>&1 | grep -E "$UNITY_BUILD_FILTER"
  local status=${PIPESTATUS[0]}
  set -e

  # ★ 終了コードを PIPESTATUS で受ける。grep を挟む以上 $? は grep のものになり、
  #   捨てると BuildScript の失敗が消えて成果物の有無だけの判定にすり替わる。
  if [ "$status" -ne 0 ]; then
    /bin/rm -rf "$output"
    if [ -s "$log" ]; then
      echo "Build failed (exit=$status). Full log: $log" >&2
    else
      echo "Build failed (exit=$status). Unity was not started" >&2
    fi
    # ★ 退避先を消してから戻すこと。残したまま次に Unity を回すと、同じ GUID のアセットが
    #   2箇所にあることになり、**Unity が原本の GUID を振り直す**（参照している側が壊れる）。
    #   退避先は .gitignore 済みで git status に出ないので、ここで名指しする。
    if [ -d "$PROJECT_PATH/Assets/XR/Temp" ]; then
      echo "The XR settings are still stashed in Assets/XR/Temp/. Restore them in this order:" >&2
      echo "  rm -rf chatter-mascot/Assets/XR/Temp" >&2
      echo "  git checkout -- chatter-mascot/Assets/XR chatter-mascot/ProjectSettings/ProjectSettings.asset" >&2
    fi
    return "$status"
  fi

  # 終了コードが 0 でも成果物が無いことはある（出力先の書き込み失敗など）
  if [ ! -e "$output" ]; then
    echo "Exit code was 0 but $output does not exist" >&2
    return 1
  fi

  echo "Done: $output"
}

# release ビルドの関門。成果物とコミットを1対1にするため、作業ツリーがきれいで、
# HEAD に mascot-v* タグが1つだけ付いていて、それが最新の版のときだけ通す。
# 版の形式の検証は BuildScript 側に任せる（規則を1か所にだけ置く）。
# 通ったら CHATTER_MASCOT_RELEASE_TAG に入れて export する。Unity を起動してからでは遅い失敗はここで先に止める。
# タグの確かめ方は chatter-agent-app/scripts/build-release.sh と同じ形。直すときは両方を揃える。
require_release_tag() {
  if [[ -n "$(git -C "$PROJECT_PATH" status --porcelain)" ]]; then
    echo "The working tree has uncommitted changes. Release builds run only from a committed state" >&2
    exit 1
  fi
  local tags
  tags="$(git -C "$PROJECT_PATH" tag --points-at HEAD --list 'mascot-v*')"
  if [[ -z "$tags" ]]; then
    echo "HEAD has no mascot-v* tag. Add one with git tag mascot-v<MAJOR>.<MINOR>.<PATCH> and run again (docs/mascot.md \"署名と版\")" >&2
    exit 1
  fi
  if [[ "$tags" == *$'\n'* ]]; then
    echo "HEAD has more than one mascot-v* tag. Leave only one:" >&2
    echo "$tags" >&2
    exit 1
  fi

  # 前回より小さい版を配ると、端末が上書きを拒んでアンインストール（再ペアリング）になる。
  # パイプで先頭だけ取ると pipefail 下で SIGPIPE になりうるので、変数展開で取る。
  local all_tags latest_tag
  all_tags="$(git -C "$PROJECT_PATH" tag --list 'mascot-v*' --sort=-v:refname)"
  latest_tag="${all_tags%%$'\n'*}"
  if [[ "$tags" != "$latest_tag" ]]; then
    echo "$tags is not the latest version. Use a tag newer than $latest_tag" >&2
    exit 1
  fi

  export CHATTER_MASCOT_RELEASE_TAG="$tags"
}
