#!/usr/bin/env bash
# Unity を batchmode で回す共通部分。
#
# ★ Editor を開いたままだと失敗する。Unity はプロジェクトを排他ロックする。
# ★ batchmode を使う理由は速さではなく、**モーダルダイアログが出ないこと**。
#   Editor 経由（MCP など）でビルドすると、保存確認ダイアログが出た瞬間に応答が返らなくなり、
#   呼び出し側からは「ハングした」としか見えない。
set -euo pipefail

UNITY_VERSION="${UNITY_VERSION:-6000.5.8f1}"
UNITY_BIN="/Applications/Unity/Hub/Editor/${UNITY_VERSION}/Unity.app/Contents/MacOS/Unity"
PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if [ ! -x "$UNITY_BIN" ]; then
  echo "Unity が見つかりません: $UNITY_BIN" >&2
  echo "UNITY_VERSION で指定できます（現在: $UNITY_VERSION）" >&2
  exit 1
fi

if pgrep -f "Unity.app/Contents/MacOS/Unity.*${PROJECT_PATH}" >/dev/null 2>&1; then
  echo "Unity Editor がこのプロジェクトを開いています。閉じてから実行してください" >&2
  exit 1
fi

run_unity() {
  "$UNITY_BIN" -batchmode -nographics -projectPath "$PROJECT_PATH" -logFile - "$@"
}
