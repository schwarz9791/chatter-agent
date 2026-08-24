#!/usr/bin/env bash
# macOS Standalone をビルドする。
#
#   ./scripts/build.sh                                        # 本番シーン
#   ./scripts/build.sh Assets/Scenes/TransparencyProbe.unity Build/TransparencyProbe.app
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.app}"

# ★ **終了コードを捨てないこと。** `| grep ... || true` にすると BuildScript の
#   EditorApplication.Exit(1) が消え、成果物の有無だけで判定することになる。
#   一度でも成功していれば古い .app が残っているので、**コンパイルエラーでも
#   「できました」と言って exit 0 する**（＝直っていないバイナリを直ったつもりで起動する）。
#   test.sh と同じ PIPESTATUS の形に揃える。
set +e
run_unity -quit \
  -executeMethod ChatterMascot.EditorTools.BuildScript.BuildMacOS \
  -buildScene "$SCENE" \
  -buildOutput "$OUTPUT" \
  2>&1 | grep -E "^\[Build\]|error CS|Error building|Exception|BuildFailedException"
STATUS=${PIPESTATUS[0]}
set -e

if [ "$STATUS" -ne 0 ]; then
  echo "ビルドに失敗しました (exit=$STATUS)" >&2
  exit "$STATUS"
fi

# ★ BuildScript は絶対パスの -buildOutput も許容する（Path.IsPathRooted）ので、
#   無条件に $PROJECT_PATH/ を前置しない
case "$OUTPUT" in
  /*) BUILT="$OUTPUT" ;;
  *)  BUILT="$PROJECT_PATH/$OUTPUT" ;;
esac

# 終了コードが 0 でも成果物が無いことはある（出力先の書き込み失敗など）
if [ ! -d "$BUILT" ]; then
  echo "終了コードは 0 ですが $BUILT がありません" >&2
  exit 1
fi

echo "できました: $BUILT"
