#!/usr/bin/env bash
# 同梱モーションを作り直す。カテゴリごとのスクリプトを Blender で回し、書き出した .vrma を
# StreamingAssets/animations/<category>/ に置く。
# プレビュー（sheet.png・preview.mp4）は out/<category>/ に残る。
#
# ./build.sh                 7本すべて
# ./build.sh walk idle       指定したカテゴリだけ
# BLENDER=/path/to/blender ./build.sh
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
blender="${BLENDER:-blender}"
bundled="$here/../Assets/StreamingAssets/animations"

if [ $# -eq 0 ]; then
  set -- happy sad angry relaxed surprised idle walk
fi

for category in "$@"; do
  script="$here/$category.py"
  if [ ! -f "$script" ]; then
    echo "Unknown category: $category" >&2
    exit 2
  fi
  out="$here/out/$category"
  mkdir -p "$out"
  rm -f "$out"/*.vrma
  echo "==> $category"
  "$blender" -b --python-exit-code 1 --python "$script" -- "$out"

  vrma=("$out"/*.vrma)
  if [ "${#vrma[@]}" -ne 1 ] || [ ! -f "${vrma[0]}" ]; then
    echo "Expected exactly one .vrma in $out" >&2
    exit 1
  fi
  dest="$bundled/$category"
  mkdir -p "$dest"
  rm -f "$dest"/*.vrma
  cp "${vrma[0]}" "$dest/"
  echo "    $(basename "${vrma[0]}") -> StreamingAssets/animations/$category/"
done
