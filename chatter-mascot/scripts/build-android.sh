#!/usr/bin/env bash
# Android の APK をビルドする（#97）。
#
#   ./scripts/build-android.sh
#   ./scripts/build-android.sh Assets/Scenes/Mascot.unity Build/ChatterMascot.apk
#   CHATTER_MASCOT_KEYSTORE=<keystore の絶対パス> ./scripts/build-android.sh   # release 署名。HEAD に mascot-v<版> タグが要る
#
# ★ AudioManager.asset の trap は無い。 build.sh の trap は BuildScript.BuildMacOS が
#   ビルド中だけ Disable Unity Audio を ON にすることの後始末だが、BuildAndroid は
#   その切り替えを一切行わない（Android は m_DisableAudio: 0 の出荷値のまま鳴らす）ので、
#   戻す対象がそもそも無い。
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.apk}"

assert_notice_in_sync

# release は APK とコミットを1対1にするため、作業ツリーがきれいで HEAD にタグが1つだけ付いているときだけ通す。
# 版の規則の検証は BuildScript 側に任せる（規則を2か所に書かない）。
if [[ -n "${CHATTER_MASCOT_KEYSTORE:-}" ]]; then
  if [[ -n "$(git -C "$PROJECT_PATH" status --porcelain)" ]]; then
    echo "作業ツリーに未コミットの変更があります。release ビルドはコミット済みの状態でだけ行います。" >&2
    exit 1
  fi
  tags="$(git -C "$PROJECT_PATH" tag --points-at HEAD --list 'mascot-v*')"
  if [[ -z "$tags" ]]; then
    echo "HEAD に mascot-v* タグがありません。git tag mascot-v<MAJOR>.<MINOR>.<PATCH> を付けてから実行してください（docs/mascot.md「署名と版」）。" >&2
    exit 1
  fi
  if [[ "$tags" == *$'\n'* ]]; then
    echo "HEAD に mascot-v* タグが複数付いています。1つにしてください:" >&2
    echo "$tags" >&2
    exit 1
  fi
  export CHATTER_MASCOT_RELEASE_TAG="$tags"
fi

# release 署名のパスワードは argv に出さず、環境変数として Unity へ継承させるだけにする。
# 未指定ならキーチェーンから読む（→ docs/mascot.md「署名と版」）。
if [[ -n "${CHATTER_MASCOT_KEYSTORE:-}" && -z "${CHATTER_MASCOT_KEYSTORE_PASS:-}" ]]; then
  if ! keystore_pass="$(security find-generic-password -a "$USER" -s chatter-mascot-keystore -w 2>/dev/null)"; then
    echo "キーチェーンに chatter-mascot-keystore の項目がありません。" \
         "CHATTER_MASCOT_KEYSTORE_PASS を設定するか、docs/mascot.md の「署名と版」で登録してください。" >&2
    exit 1
  fi
  export CHATTER_MASCOT_KEYSTORE_PASS="$keystore_pass"
  unset keystore_pass
fi

# ★ Android 専用フラグ（--android-export-type など）を足さないこと。出力形式の書き手を増やすと、
#   honor されなかったときに aab が .apk という名前で出る（→ docs/knowledge/mascot-unity.md「★ Unity CLI」）。
unity_build_player Android ChatterMascot.EditorTools.BuildScript.BuildAndroid \
  "$OUTPUT" Logs/build-android.log "$SCENE"
