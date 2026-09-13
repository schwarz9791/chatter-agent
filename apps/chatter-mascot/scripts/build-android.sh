#!/usr/bin/env bash
# Android の APK をビルドする（#97）。
#
#   ./scripts/build-android.sh
#   ./scripts/build-android.sh Assets/Scenes/Mascot.unity Build/ChatterMascot.apk
#
# ★ build-native.sh は呼ばないこと。ネイティブプラグイン（ChatterMascotNative.bundle）は
#   macOS 専用の常駐機能（メニューバー・グローバルショートカット）のためのもので、
#   Android には積まない（NativePluginSettings が macOS だけに絞っている）。
#
# ★ AudioManager.asset の trap も無い。 build.sh の trap は BuildScript.BuildMacOS が
#   ビルド中だけ Disable Unity Audio を ON にすることの後始末だが、BuildAndroid は
#   その切り替えを一切行わない（Android は m_DisableAudio: 0 の出荷値のまま鳴らす）ので、
#   戻す対象がそもそも無い。
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.apk}"

# ★ **終了コードを捨てないこと。** build.sh / test.sh と同じ PIPESTATUS の形に揃える。
set +e
run_unity -quit -buildTarget Android \
  -executeMethod ChatterMascot.EditorTools.BuildScript.BuildAndroid \
  -buildScene "$SCENE" \
  -buildOutput "$OUTPUT" \
  2>&1 | grep -E "^\[Build\]|^\[Icon\]|error CS|Error building|Exception|BuildFailedException|Project has invalid dependencies|An error occurred while resolving packages"
STATUS=${PIPESTATUS[0]}
set -e

if [ "$STATUS" -ne 0 ]; then
  echo "ビルドに失敗しました (exit=$STATUS)" >&2
  exit "$STATUS"
fi

case "$OUTPUT" in
  /*) BUILT="$OUTPUT" ;;
  *)  BUILT="$PROJECT_PATH/$OUTPUT" ;;
esac

# 終了コードが 0 でも成果物が無いことはある（出力先の書き込み失敗など）
if [ ! -f "$BUILT" ]; then
  echo "終了コードは 0 ですが $BUILT がありません" >&2
  exit 1
fi

echo "できました: $BUILT"
