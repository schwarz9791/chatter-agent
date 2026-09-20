#!/usr/bin/env bash
# 任意の Editor メソッドを batchmode で回す。
#
#   ./scripts/run.sh ChatterMascot.EditorTools.SceneFixups.FixAll
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

METHOD="${1:?メソッド名を指定してください}"
shift || true

RUN_LOG="$PROJECT_PATH/Logs/run.log"
mkdir -p "$PROJECT_PATH/Logs"

# ★ ここに無いプレフィックスのログは LogError であっても画面に出ない。
#   メソッドを増やしたらパターンも足すこと。
#   `Failed to resolve` / `Cannot perform upm operation` はパッケージ解決の失敗
#   （UniVRM を manifest.json に足した直後の初回解決で踏む）。
#   `Project has invalid dependencies` / `An error occurred while resolving packages` は
#   manifest.json が存在しないビルトインモジュールを指しているときの失敗で、
#   上の2つとは文言が別（Editor バージョンを跨ぐ変更で踏む）。
#   フィルタに掛からないログも $RUN_LOG に全文が残る。
#
# ★ **終了コードを捨てないこと。** `| grep ... || true` にすると、呼び出した
#   Editor メソッドの EditorApplication.Exit(1)（例: IconSettings.FixAll がアセット欠落で
#   落ちるケース）が消え、常に exit 0 になる。set -e のスクリプトや && の連鎖、CI が
#   失敗を成功として扱ってしまう。grep が1件も拾わなかったときの exit 1 が紛れ込まないよう
#   PIPESTATUS で受ける（build.sh と同じ形）
# ★ -quit は渡さない。unity run が自分で予約フラグとして付けるため、渡すと起動前に弾かれる。
set +e
unity run "$PROJECT_PATH" --no-banner \
  -- -nographics -logFile - -executeMethod "$METHOD" "$@" 2>&1 \
  | tee "$RUN_LOG" \
  | grep -E "^\[Fixups\]|^\[Build\]|^\[Native\]|^\[VrmProbe\]|^\[Icon\]|error CS|Aborting batchmode|Unhandled exception|Failed to resolve|Cannot perform upm operation|Project has invalid dependencies|An error occurred while resolving packages"
STATUS=${PIPESTATUS[0]}
set -e

if [ "$STATUS" -ne 0 ]; then
  echo "失敗しました (exit=$STATUS)。全文は $RUN_LOG" >&2
fi
exit $STATUS
