#!/usr/bin/env bash
# macOS Standalone をビルドする。
#
#   ./scripts/build.sh                                        # 本番シーン
#   ./scripts/build.sh Assets/Scenes/TransparencyProbe.unity Build/TransparencyProbe.app
#   CHATTER_MASCOT_RELEASE=1 ./scripts/build.sh               # release。HEAD に mascot-v<版> タグと CHATTER_AGENT_UPDATER_KEY が要る → Build/ChatterMascot-macOS.zip と .sig
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
    echo "Interruption detected, so Disable Unity Audio was reset to the shipping value" >&2
  fi
}

# macOS には署名鍵が無く、Android のように keystore の有無で release を見分けられないので、明示のフラグで切り替える。
# ★ 関門は trap を張る前に通すこと。後にすると、汚れた作業ツリーで止まったときに trap が手元の変更を消す。
RELEASE="${CHATTER_MASCOT_RELEASE:-}"
# 版は require_release_tag が通したときだけ BuildScript に渡す（環境に残っていても手元ビルドに配る版を入れない）
unset CHATTER_MASCOT_RELEASE_TAG
zip="$PROJECT_PATH/Build/ChatterMascot-macOS.zip"
if [[ -n "$RELEASE" ]]; then
  require_release_tag

  # updater の署名鍵の関門。chatter-agent-app/scripts/build-release.sh の同名のブロックと同じ形。直すときは両方を揃える。
  # パスワードはシェル変数に持ち、署名のコマンドにだけ環境変数で渡す（Unity のビルドには見せない）。
  if [[ -z "${CHATTER_AGENT_UPDATER_KEY:-}" ]]; then
    echo "CHATTER_AGENT_UPDATER_KEY is not set. Set it to the absolute path of the updater private key (docs/release.md)" >&2
    exit 1
  fi
  if [[ ! -f "$CHATTER_AGENT_UPDATER_KEY" ]]; then
    echo "Updater key not found: $CHATTER_AGENT_UPDATER_KEY" >&2
    exit 1
  fi
  CHATTER_AGENT_UPDATER_KEY="$(/bin/realpath "$CHATTER_AGENT_UPDATER_KEY")"
  unset key_pass
  key_pass="${TAURI_SIGNING_PRIVATE_KEY_PASSWORD:-}"
  unset TAURI_SIGNING_PRIVATE_KEY_PASSWORD TAURI_SIGNING_PRIVATE_KEY TAURI_SIGNING_PRIVATE_KEY_PATH
  if [[ -z "$key_pass" ]]; then
    if ! key_pass="$(security find-generic-password -a "$USER" -s chatter-agent-updater-key -w)"; then
      echo "Could not read the chatter-agent-updater-key password from the keychain." \
           "If the item does not exist, register it following docs/release.md, or set TAURI_SIGNING_PRIVATE_KEY_PASSWORD." >&2
      exit 1
    fi
  fi

  # macOS 版の zip の署名には ChatterAgent の tauri CLI を使う。
  tauri_bin="$PROJECT_PATH/../chatter-agent-app/node_modules/.bin/tauri"
  if [[ ! -x "$tauri_bin" ]]; then
    echo "tauri CLI not found: $tauri_bin. Run npm --prefix ../chatter-agent-app ci" >&2
    exit 1
  fi

  # 失敗したときに前回の zip と署名が残っていると、それを配ってしまう
  /bin/rm -f "$zip" "$zip.sig"
fi

# release は ProjectSettings.asset に入れた版を残さないため、git から戻す（作業ツリーがきれいなことは確かめ済み）。
# trap は1シグナルに1つしか効かないので、まとめて1つにする。
cleanup() {
  restore_audio_manager
  if [[ -n "$RELEASE" ]]; then
    git -C "$PROJECT_PATH" checkout -- ProjectSettings/ProjectSettings.asset 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

unity_build_player StandaloneOSX ChatterMascot.EditorTools.BuildScript.BuildMacOS \
  "$OUTPUT" Logs/build-macos.log "$SCENE"

if [[ -n "$RELEASE" ]]; then
  case "$OUTPUT" in
    /*) app="$OUTPUT" ;;
    *)  app="$PROJECT_PATH/$OUTPUT" ;;
  esac

  # 配る前に封印が生きていることを確かめる。壊れていると、ダウンロードしたコピーは「壊れている」と扱われる。
  if ! codesign --verify --deep --strict "$app"; then
    echo "The code signature seal is broken: $app" >&2
    exit 1
  fi

  # 版と LSUIElement は .app から読んで確かめる。Unity が入れ損ねても、配る前にここで止める。
  plist="$app/Contents/Info.plist"
  version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$plist")"
  if [[ "$version" != "${CHATTER_MASCOT_RELEASE_TAG#mascot-v}" ]]; then
    echo "Version does not match the tag: Info.plist=$version tag=$CHATTER_MASCOT_RELEASE_TAG" >&2
    exit 1
  fi
  if [[ "$(/usr/libexec/PlistBuddy -c 'Print :LSUIElement' "$plist" 2>/dev/null)" != "true" ]]; then
    echo "LSUIElement is missing (the app would appear in the Dock): $plist" >&2
    exit 1
  fi

  # ★ 拡張属性を zip に入れない（--norsrc）。AppleDouble の ._* がバンドルの中に入ると、
  #   ditto 以外で展開したときに封印が壊れる。chatter-agent-app/scripts/build-release.sh と同じ。
  ditto -c -k --norsrc --keepParent "$app" "$zip"
  # ChatterAgent が更新を検証する署名。
  TAURI_SIGNING_PRIVATE_KEY_PASSWORD="$key_pass" "$tauri_bin" signer sign -f "$CHATTER_AGENT_UPDATER_KEY" "$zip"
  echo "[Build] version=$version → Build/ChatterMascot-macOS.zip Build/ChatterMascot-macOS.zip.sig"
fi
