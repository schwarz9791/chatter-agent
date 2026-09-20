#!/usr/bin/env bash
# Unity を batchmode で回す共通部分。
#
# ★ Editor を開いたままだと失敗する。Unity はプロジェクトを排他ロックする。
# ★ batchmode を使う理由は速さではなく、**モーダルダイアログが出ないこと**。
#   Editor 経由（MCP など）でビルドすると、保存確認ダイアログが出た瞬間に応答が返らなくなり、
#   呼び出し側からは「ハングした」としか見えない。
set -euo pipefail

PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ -n "${UNITY_VERSION:-}" ]; then
  # ★ 明示指定はそれだけを見る。サフィックス違いへのフォールバックはしない。
  CANDIDATES=("$UNITY_VERSION")
else
  # ★ 版の書き場所を ProjectSettings/ProjectVersion.txt の1つにする。
  #   版を切り替えるたびにこのスクリプトを直す必要がなくなる。
  VERSION_FILE="$PROJECT_PATH/ProjectSettings/ProjectVersion.txt"
  PROJECT_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$VERSION_FILE" | head -1)"
  if [ -z "$PROJECT_VERSION" ]; then
    echo "m_EditorVersion を読めません: $VERSION_FILE" >&2
    exit 1
  fi
  CANDIDATES=("$PROJECT_VERSION" "${PROJECT_VERSION}-arm64")
fi

UNITY_BIN=""
for candidate in "${CANDIDATES[@]}"; do
  bin="/Applications/Unity/Hub/Editor/${candidate}/Unity.app/Contents/MacOS/Unity"
  if [ -x "$bin" ]; then
    UNITY_BIN="$bin"
    break
  fi
done

if [ -z "$UNITY_BIN" ]; then
  echo "Unity が見つかりません。探した場所:" >&2
  for candidate in "${CANDIDATES[@]}"; do
    echo "  /Applications/Unity/Hub/Editor/${candidate}/Unity.app/Contents/MacOS/Unity" >&2
  done
  echo "UNITY_VERSION で指定できます" >&2
  exit 1
fi

if pgrep -f "Unity.app/Contents/MacOS/Unity.*${PROJECT_PATH}" >/dev/null 2>&1; then
  echo "Unity Editor がこのプロジェクトを開いています。閉じてから実行してください" >&2
  exit 1
fi

run_unity() {
  "$UNITY_BIN" -batchmode -nographics -projectPath "$PROJECT_PATH" -logFile - "$@"
}

# ★ 出荷物に古いライセンス表記を入れないための関門。ビルドするスクリプトが Unity を呼ぶ前に呼ぶ。
#   同梱コピーは消せない（`.app` からリポジトリの NOTICE は見えず、設定パネルは Editor からも読む）ので、
#   **コピーするのではなく一致を確かめる** —— ビルドが追跡ファイルを書き換える形にすると、
#   中断やクラッシュで食い違ったまま残る経路ができる（→ ProjectSettings/AudioManager.asset の3段構え）。
assert_notice_in_sync() {
  local source="$PROJECT_PATH/../../NOTICE"
  local copy="$PROJECT_PATH/Assets/StreamingAssets/NOTICE.txt"
  if ! diff -q "$source" "$copy" >/dev/null; then
    echo "同梱の NOTICE.txt がリポジトリの NOTICE とズレています。次で合わせてから出し直してください:" >&2
    echo "  cp NOTICE apps/chatter-mascot/Assets/StreamingAssets/NOTICE.txt" >&2
    diff -u "$source" "$copy" >&2 || true
    exit 1
  fi
}
