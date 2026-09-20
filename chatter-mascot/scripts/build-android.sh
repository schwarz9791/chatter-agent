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

# ★ Unity.app を直に叩くのはこのスクリプトだけ。他の3本は Unity CLI 経由で、
#   どの Editor を使うかは CLI が ProjectVersion.txt から解決する。
#   Android を Unity CLI へ寄せたら、ここは丸ごと消える
#   （→ docs/knowledge/mascot-unity.md「★ Unity CLI」）。
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

# ★ ここで止めること。run_unity の中から止めると、下の `2>&1 | grep` に飲まれて
#   理由が画面に出ないまま「ビルドに失敗しました」だけが残る。
if [ -z "$UNITY_BIN" ]; then
  echo "Unity が見つかりません。探した場所:" >&2
  for candidate in "${CANDIDATES[@]}"; do
    echo "  /Applications/Unity/Hub/Editor/${candidate}/Unity.app/Contents/MacOS/Unity" >&2
  done
  echo "UNITY_VERSION で指定できます" >&2
  exit 1
fi

run_unity() {
  "$UNITY_BIN" -batchmode -nographics -projectPath "$PROJECT_PATH" -logFile - "$@"
}

assert_notice_in_sync

case "$OUTPUT" in
  /*) BUILT="$OUTPUT" ;;
  *)  BUILT="$PROJECT_PATH/$OUTPUT" ;;
esac

# ★ **終了コードを捨てないこと。** build.sh / run.sh と同じ PIPESTATUS の形に揃える。
set +e
run_unity -quit -buildTarget Android \
  -executeMethod ChatterMascot.EditorTools.BuildScript.BuildAndroid \
  -buildScene "$SCENE" \
  -buildOutput "$OUTPUT" \
  2>&1 | grep -E "^\[Build\]|^\[Icon\]|error CS|Error building|Exception|BuildFailedException|Project has invalid dependencies|An error occurred while resolving packages"
STATUS=${PIPESTATUS[0]}
set -e

# ★ 失敗したら成果物を消す。ビルドの後処理（AndroidManifestPostProcessor など）が
#   BuildFailedException で止めても、Unity は APK を書き出してから失敗を返す。
#   残すと run-android.sh が後処理の抜けた APK をそのまま入れる。
if [ "$STATUS" -ne 0 ]; then
  /bin/rm -f "$BUILT"
  echo "ビルドに失敗しました (exit=$STATUS)" >&2
  exit "$STATUS"
fi

# 終了コードが 0 でも成果物が無いことはある（出力先の書き込み失敗など）
if [ ! -f "$BUILT" ]; then
  echo "終了コードは 0 ですが $BUILT がありません" >&2
  exit 1
fi

echo "できました: $BUILT"
