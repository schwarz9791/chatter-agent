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
run_unity -quit -executeMethod "$METHOD" "$@" 2>&1 \
  | grep -E "^\[Fixups\]|^\[Build\]|^\[Native\]|^\[VrmProbe\]|^\[VrmaExport\]|error CS|Aborting batchmode|Unhandled exception|Failed to resolve|Cannot perform upm operation" || true
