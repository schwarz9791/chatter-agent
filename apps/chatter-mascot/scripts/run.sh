#!/usr/bin/env bash
# 任意の Editor メソッドを batchmode で回す。
#
#   ./scripts/run.sh ChatterMascot.EditorTools.SceneFixups.FixAll
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

METHOD="${1:?メソッド名を指定してください}"
shift || true

# ★ ここに無いプレフィックスのログは LogError であっても画面に出ない。
#   メソッドを増やしたらパターンも足すこと。
#   `Failed to resolve` / `Cannot perform upm operation` はパッケージ解決の失敗
#   （UniVRM を manifest.json に足した直後の初回解決で踏む）。
#
# ★ **終了コードを捨てないこと。** `| grep ... || true` にすると、呼び出した
#   Editor メソッドの EditorApplication.Exit(1)（例: IconSettings.FixAll がアセット欠落で
#   落ちるケース）が消え、常に exit 0 になる。set -e のスクリプトや && の連鎖、CI が
#   失敗を成功として扱ってしまう。grep が1件も拾わなかったときの exit 1 が紛れ込まないよう
#   PIPESTATUS で受ける（test.sh / build.sh と同じ形）
set +e
run_unity -quit -executeMethod "$METHOD" "$@" 2>&1 \
  | grep -E "^\[Fixups\]|^\[Build\]|^\[Native\]|^\[VrmProbe\]|^\[Icon\]|error CS|Aborting batchmode|Unhandled exception|Failed to resolve|Cannot perform upm operation"
STATUS=${PIPESTATUS[0]}
set -e
exit $STATUS
