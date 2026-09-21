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
  echo "Unity Editor がこのプロジェクトを開いています。閉じてから実行してください" >&2
  exit 1
fi

if ! command -v unity >/dev/null 2>&1; then
  echo "unity コマンドが見つかりません。次でインストールしてください:" >&2
  echo "  curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash" >&2
  exit 1
fi

# ★ Unity を起動する前にネイティブプラグインの実体を用意すること。
#   .bundle は git に入れていないのでクリーンなツリーには .meta しか無く、この状態で Unity を
#   起動すると孤児として .meta が捨てられる。あとからバンドルを作ると別の GUID で再インポートされ、
#   参照している側が壊れる（→ docs/knowledge/mascot-unity.md）。
#
# ★ 既にあるときは作り直さない。ここは Unity を回すすべてのスクリプトが通るので、無条件に
#   clang を走らせるとテストの起動が毎回遅くなる。ソースの変更を拾うのは build.sh の役目。
#
# ★ 失敗しても止めない。バンドルが無くても Unity は回る（常駐機能だけが落ちる）。
#   .meta は build-native.sh が入れ物を残すことで守られる。
NATIVE_BUNDLE_BIN="$PROJECT_PATH/Assets/Plugins/macOS/ChatterMascotNative.bundle/Contents/MacOS/ChatterMascotNative"
if [ ! -f "$NATIVE_BUNDLE_BIN" ]; then
  if ! "$(dirname "${BASH_SOURCE[0]}")/build-native.sh"; then
    echo "[Native] バンドルを作れませんでした。常駐機能は動きません" >&2
  fi
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
    echo "同梱の NOTICE.txt がリポジトリの NOTICE とズレています。次で合わせてから出し直してください:" >&2
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
      echo "ビルドに失敗しました (exit=$status)。全文は $log" >&2
    else
      echo "ビルドに失敗しました (exit=$status)。Unity は起動していません" >&2
    fi
    # ★ 退避先を消してから戻すこと。残したまま次に Unity を回すと、同じ GUID のアセットが
    #   2箇所にあることになり、**Unity が原本の GUID を振り直す**（参照している側が壊れる）。
    #   退避先は .gitignore 済みで git status に出ないので、ここで名指しする。
    if [ -d "$PROJECT_PATH/Assets/XR/Temp" ]; then
      echo "XR の設定が Assets/XR/Temp/ に退避されたままです。次の順で戻してください:" >&2
      echo "  rm -rf chatter-mascot/Assets/XR/Temp" >&2
      echo "  git checkout -- chatter-mascot/Assets/XR chatter-mascot/ProjectSettings/ProjectSettings.asset" >&2
    fi
    return "$status"
  fi

  # 終了コードが 0 でも成果物が無いことはある（出力先の書き込み失敗など）
  if [ ! -e "$output" ]; then
    echo "終了コードは 0 ですが $output がありません" >&2
    return 1
  fi

  echo "できました: $output"
}
