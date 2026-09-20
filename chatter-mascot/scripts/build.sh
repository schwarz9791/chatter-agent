#!/usr/bin/env bash
# macOS Standalone をビルドする。
#
#   ./scripts/build.sh                                        # 本番シーン
#   ./scripts/build.sh Assets/Scenes/TransparencyProbe.unity Build/TransparencyProbe.app
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.app}"

# ★ BuildScript は絶対パスの -buildOutput も許容する（Path.IsPathRooted）ので、
#   無条件に $PROJECT_PATH/ を前置しない
# ★ unity build の --output-path はカレントディレクトリ基準で解決するので、
#   相対パスのまま渡すと呼び出し元のディレクトリ次第で出力先がズレる。ここで絶対パスにしておく。
case "$OUTPUT" in
  /*) BUILT="$OUTPUT" ;;
  *)  BUILT="$PROJECT_PATH/$OUTPUT" ;;
esac

BUILD_LOG="$PROJECT_PATH/Logs/build-macos.log"
mkdir -p "$PROJECT_PATH/Logs"
# ★ 前回のログを空にしてから走らせること。unity build は --log-file に追記し、
#   そのログを画面へも流すので、消さないと前回のビルドの行が今回の出力に混ざる。
: > "$BUILD_LOG"

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
    # macOS 専用スクリプト（unity.sh が /Applications/Unity/… を見ている）なので BSD sed
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

# ★ **終了コードを捨てないこと。** `| grep ... || true` にすると BuildScript の
#   EditorApplication.Exit(1) が消え、成果物の有無だけで判定することになる。
#   一度でも成功していれば古い .app が残っているので、**コンパイルエラーでも
#   「できました」と言って exit 0 する**（＝直っていないバイナリを直ったつもりで起動する）。
#   grep を挟む以上 $? は grep のものになるので PIPESTATUS で受ける（run.sh と同じ形）。
# ★ --target StandaloneOSX を明示する。アクティブなビルドターゲットが Android のまま
#   残っていた場合、指定しないと BuildPlayer の中で切り替えと再インポートを待つ。
set +e
unity build "$PROJECT_PATH" --target StandaloneOSX \
  --execute-method ChatterMascot.EditorTools.BuildScript.BuildMacOS \
  -o "$BUILT" \
  --args "-buildScene $SCENE" \
  --log-file "$BUILD_LOG" --no-banner --no-provenance \
  2>&1 | grep -E "^\[Build\]|^\[Native\]|error CS|Error building|Exception|BuildFailedException|Project has invalid dependencies|An error occurred while resolving packages"
STATUS=${PIPESTATUS[0]}
set -e

if [ "$STATUS" -ne 0 ]; then
  echo "ビルドに失敗しました (exit=$STATUS)。全文は $BUILD_LOG" >&2
  exit "$STATUS"
fi

# 終了コードが 0 でも成果物が無いことはある（出力先の書き込み失敗など）
if [ ! -d "$BUILT" ]; then
  echo "終了コードは 0 ですが $BUILT がありません" >&2
  exit 1
fi

echo "できました: $BUILT"
