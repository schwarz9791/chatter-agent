#!/usr/bin/env bash
# 配る ChatterAgent（build/ChatterAgent.zip）をビルドする（#191）。
#
#   HEAD に agent-v<MAJOR>.<MINOR>.<PATCH> タグを付けてから、npm run build:release
#
# 版はタグから決まる。追跡ファイルは書き換えない（コミットされている版は手元ビルドの印 0.0.0-dev）。
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# 配るものとコミットを1対1にするため、作業ツリーがきれいで HEAD にタグが1つだけ付いているときだけ通す。
# chatter-mascot/scripts/build-android.sh の release 分岐と同じ形。直すときは両方を揃える。
if [[ -n "$(git status --porcelain)" ]]; then
  echo "作業ツリーに未コミットの変更があります。release ビルドはコミット済みの状態でだけ行います。" >&2
  exit 1
fi
tags="$(git tag --points-at HEAD --list 'agent-v*')"
if [[ -z "$tags" ]]; then
  echo "HEAD に agent-v* タグがありません。git tag agent-v<MAJOR>.<MINOR>.<PATCH> を付けてから実行してください（docs/release.md）。" >&2
  exit 1
fi
if [[ "$tags" == *$'\n'* ]]; then
  echo "HEAD に agent-v* タグが複数付いています。1つにしてください:" >&2
  echo "$tags" >&2
  exit 1
fi

if [[ ! "$tags" =~ ^agent-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ || "$tags" == "agent-v0.0.0" ]]; then
  echo "$tags は版の形式に合いません。agent-v<MAJOR>.<MINOR>.<PATCH>（先頭ゼロ不可・接尾辞不可・0.0.0 不可）にしてください。" >&2
  exit 1
fi
version="${tags#agent-v}"

# 前回より小さい版を配ると、新旧が逆転する。
# パイプで先頭だけ取ると pipefail 下で SIGPIPE になりうるので、変数展開で取る。
all_tags="$(git tag --list 'agent-v*' --sort=-v:refname)"
latest_tag="${all_tags%%$'\n'*}"
if [[ "$tags" != "$latest_tag" ]]; then
  echo "$tags は最新の版ではありません。$latest_tag より大きい版のタグにしてください。" >&2
  exit 1
fi

# --config は設定にマージされ、package_info().version と Info.plist に入る。
npm run tauri -- build --config "{\"version\":\"$version\"}"

rm -rf build/ChatterAgent.app build/ChatterAgent.zip
mkdir -p build
cp -R src-tauri/target/release/bundle/macos/ChatterAgent.app build/
# 拡張属性は zip に入れない。入れると AppleDouble（._*）がバンドルの中に並び、ditto 以外で展開すると署名の封が壊れる。
ditto -c -k --norsrc --keepParent build/ChatterAgent.app build/ChatterAgent.zip
echo "[Build] version=$version → build/ChatterAgent.zip"
