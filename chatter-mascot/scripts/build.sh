#!/usr/bin/env bash
# macOS Standalone をビルドする。
#
#   ./scripts/build.sh                                        # 本番シーン
#   ./scripts/build.sh Assets/Scenes/TransparencyProbe.unity Build/TransparencyProbe.app
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.app}"

assert_notice_in_sync

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
    # macOS 専用スクリプト（--target StandaloneOSX をビルドする）なので BSD sed
    sed -i '' 's/^  m_DisableAudio: 1$/  m_DisableAudio: 0/' "$AUDIO_MANAGER"
    echo "中断を検出したので Disable Unity Audio を出荷値に戻しました" >&2
  fi
}
trap restore_audio_manager EXIT INT TERM

# ★ **Unity より先にネイティブプラグインを作ること。**（#75）
#   Unity は Assets/Plugins/macOS/ に .bundle が置かれている状態でビルドする必要がある。
#
# ★ **失敗してもビルドを止めないこと。** 止めると「マスコットが出ない」に化ける。
#   バンドルが無くても起動はして、常駐機能（メニューバー・ショートカット）だけが落ちる
#   （→ Desktop/Native/ChatterMascotNative.cs）。set -e があるので明示的に受ける。
if ! "$(dirname "${BASH_SOURCE[0]}")/build-native.sh"; then
  echo "[Native] ネイティブプラグインを作れませんでした。メニューバー常駐は動きません" >&2
fi

unity_build_player StandaloneOSX ChatterMascot.EditorTools.BuildScript.BuildMacOS \
  "$OUTPUT" Logs/build-macos.log "$SCENE"
