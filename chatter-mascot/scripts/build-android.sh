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

require_unity_cli

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.apk}"

assert_notice_in_sync

# ★ --execute-method を使うと -o は -buildOutput としてそのまま渡り、相対パスを解くのは
#   BuildScript 側（プロジェクトルート基準）。解決がどこで行われるかに寄りかからないよう、
#   ここで絶対パスにしておく。BuildScript は絶対パスも許容する（Path.IsPathRooted）。
case "$OUTPUT" in
  /*) BUILT="$OUTPUT" ;;
  *)  BUILT="$PROJECT_PATH/$OUTPUT" ;;
esac

# ★ ログは Unity を呼ぶ直前に空にする。CLI は --log-file の中身を画面へも流すので、
#   残っていると前回のビルドの行が先に流れる。
BUILD_LOG="$PROJECT_PATH/Logs/build-android.log"
mkdir -p "$PROJECT_PATH/Logs"
: > "$BUILD_LOG"

# ★ 古い APK を先に消す。残っていると、失敗を 0 で返す経路が1つでもあったときに
#   下の存在チェックまで前回の成功物で通り、直っていないバイナリを直ったつもりで起動する。
/bin/rm -f "$BUILT"

# ★ ^Error: は CLI 自身の失敗（Editor が未インストール、target 不正、認証切れ）。
#   Editor が起動する前に弾かれるので --log-file には何も残らず、ここで拾わないと
#   終了コードだけが残って理由が画面から消える
# ★ **Android 専用フラグ（--android-export-type など）を足さないこと。**
#   --execute-method と併用すると CLI は BuildPlayerOptions を握らないので honor される
#   保証が無く、出力形式の書き手を増やすと **aab が .apk という名前で出る**形で黙って壊れる。
#   出力形式は BuildScript、targetSdk などは ProjectSettings が唯一の書き手
#   （→ docs/knowledge/mascot-unity.md「★ Unity CLI」）。
set +e
unity build "$PROJECT_PATH" --target Android "${UNITY_CLI_ARGS[@]}" \
  --execute-method ChatterMascot.EditorTools.BuildScript.BuildAndroid \
  -o "$BUILT" \
  --args "-buildScene $SCENE" \
  --log-file "$BUILD_LOG" --no-provenance \
  2>&1 | grep -E "^Error:|^\[Build\]|^\[Icon\]|error CS|Error building|Exception|BuildFailedException|Project has invalid dependencies|An error occurred while resolving packages"
STATUS=${PIPESTATUS[0]}
set -e

# ★ 失敗しても APK が残ることがある。ビルドの後処理（AndroidManifestPostProcessor など）が
#   BuildFailedException で止めても、Unity は APK を書き出してから失敗を返す。残すと
#   run-android.sh が後処理の抜けた APK をそのまま入れる。
if [ "$STATUS" -ne 0 ]; then
  /bin/rm -f "$BUILT"
  if [ -s "$BUILD_LOG" ]; then
    echo "ビルドに失敗しました (exit=$STATUS)。全文は $BUILD_LOG" >&2
  else
    echo "ビルドに失敗しました (exit=$STATUS)。Unity は起動していません" >&2
  fi
  exit "$STATUS"
fi

# 終了コードが 0 でも成果物が無いことはある（出力先の書き込み失敗など）
if [ ! -f "$BUILT" ]; then
  echo "終了コードは 0 ですが $BUILT がありません" >&2
  exit 1
fi

echo "できました: $BUILT"
