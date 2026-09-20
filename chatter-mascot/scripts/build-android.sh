#!/usr/bin/env bash
# Android の APK をビルドする（#97）。
#
#   ./scripts/build-android.sh
#   ./scripts/build-android.sh Assets/Scenes/Mascot.unity Build/ChatterMascot.apk
#
# ★ ここから build-native.sh は呼ばないこと。ネイティブプラグイン（ChatterMascotNative.bundle）は
#   macOS 専用の常駐機能（メニューバー・グローバルショートカット）のためのもので、
#   Android には積まない（NativePluginSettings が macOS だけに絞っている）。
#   バンドルの実体は unity.sh が用意する。Android ビルドでも Unity を起動する以上、
#   実体が無いと .bundle.meta が孤児として捨てられるため。
#
# ★ AudioManager.asset の trap も無い。 build.sh の trap は BuildScript.BuildMacOS が
#   ビルド中だけ Disable Unity Audio を ON にすることの後始末だが、BuildAndroid は
#   その切り替えを一切行わない（Android は m_DisableAudio: 0 の出荷値のまま鳴らす）ので、
#   戻す対象がそもそも無い。
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.apk}"

assert_notice_in_sync

# ★ Android 専用フラグ（--android-export-type など）を足さないこと。出力形式の書き手を増やすと、
#   honor されなかったときに aab が .apk という名前で出る（→ docs/knowledge/mascot-unity.md「★ Unity CLI」）。
unity_build_player Android ChatterMascot.EditorTools.BuildScript.BuildAndroid \
  "$OUTPUT" Logs/build-android.log "$SCENE"
