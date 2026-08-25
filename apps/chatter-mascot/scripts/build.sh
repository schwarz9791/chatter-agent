#!/usr/bin/env bash
# macOS Standalone をビルドする。
#
#   ./scripts/build.sh                                        # 本番シーン
#   ./scripts/build.sh Assets/Scenes/TransparencyProbe.unity Build/TransparencyProbe.app
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.app}"

# ★ ビルド中、BuildScript は ProjectSettings/AudioManager.asset の m_DisableAudio を ON にする
#   （macOS では Unity 内蔵オーディオが有効なままだと、AudioSource を1つも鳴らさなくても
#   出力デバイスを掴み続けるため）。BuildScript の finally で戻しているが、
#   **Ctrl-C・CI のジョブタイムアウト・クラッシュでは finally に辿り着かない**。
#   git 管理下のファイルなので、ON が残ったままコミットされると
#   **Android ビルドが Unity 内蔵オーディオごと無効になり、全発話が ack されて消える**。
#
#   3段構えの1段目がここ。2段目は BuildScript の自己修復（既に ON でも必ず出荷値に戻す）、
#   3段目は CI の assert（SIGKILL はここでも拾えないので、最後の砦が要る）。
AUDIO_MANAGER="$PROJECT_PATH/ProjectSettings/AudioManager.asset"
restore_audio_manager() {
  if [ -f "$AUDIO_MANAGER" ] && grep -qx "  m_DisableAudio: 1" "$AUDIO_MANAGER"; then
    # macOS 専用スクリプト（unity.sh が /Applications/Unity/… を見ている）なので BSD sed
    sed -i '' 's/^  m_DisableAudio: 1$/  m_DisableAudio: 0/' "$AUDIO_MANAGER"
    echo "中断を検出したので Disable Unity Audio を出荷値に戻しました" >&2
  fi
}
trap restore_audio_manager EXIT INT TERM

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
