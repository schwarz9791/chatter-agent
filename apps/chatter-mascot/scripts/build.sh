#!/usr/bin/env bash
# macOS Standalone をビルドする。
#
#   ./scripts/build.sh                                        # 本番シーン
#   ./scripts/build.sh Assets/Scenes/TransparencyProbe.unity Build/TransparencyProbe.app
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.app}"

run_unity -quit \
  -executeMethod ChatterMascot.EditorTools.BuildScript.BuildMacOS \
  -buildScene "$SCENE" \
  -buildOutput "$OUTPUT" \
  2>&1 | grep -E "^\[Build\]|error CS|Error building|Exception|BuildFailedException" || true

if [ -d "$PROJECT_PATH/$OUTPUT" ]; then
  echo "できました: $OUTPUT"
else
  echo "ビルドに失敗しました" >&2
  exit 1
fi
