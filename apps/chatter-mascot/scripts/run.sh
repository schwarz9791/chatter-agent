#!/usr/bin/env bash
# 任意の Editor メソッドを batchmode で回す。
#
#   ./scripts/run.sh ChatterMascot.EditorTools.SceneFixups.FixAll
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

METHOD="${1:?メソッド名を指定してください}"
shift || true

run_unity -quit -executeMethod "$METHOD" "$@" 2>&1 \
  | grep -E "^\[Fixups\]|^\[Build\]|error CS|Aborting batchmode|Unhandled exception" || true
