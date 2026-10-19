#!/usr/bin/env bash
# 配る ChatterAgent（build/ChatterAgent.zip）をビルドする（#191）。
#
#   HEAD に agent-v<MAJOR>.<MINOR>.<PATCH> タグを付けてから、npm run build:release
#
# 版はタグから決まる。追跡ファイルは書き換えない（コミットされている版は手元ビルドの印 0.0.0-dev）。
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# 配るものとコミットを1対1にするため、作業ツリーがきれいで HEAD にタグが1つだけ付いているときだけ通す。
# chatter-mascot/scripts/unity.sh の require_release_tag と同じ形。直すときは両方を揃える。
if [[ -n "$(git status --porcelain)" ]]; then
  echo "The working tree has uncommitted changes. Release builds run only from a committed state" >&2
  exit 1
fi
tags="$(git tag --points-at HEAD --list 'agent-v*')"
if [[ -z "$tags" ]]; then
  echo "HEAD has no agent-v* tag. Add one with git tag agent-v<MAJOR>.<MINOR>.<PATCH> and run again (docs/release.md)" >&2
  exit 1
fi
if [[ "$tags" == *$'\n'* ]]; then
  echo "HEAD has more than one agent-v* tag. Leave only one:" >&2
  echo "$tags" >&2
  exit 1
fi

if [[ ! "$tags" =~ ^agent-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ || "$tags" == "agent-v0.0.0" ]]; then
  echo "$tags does not match the version format. Use agent-v<MAJOR>.<MINOR>.<PATCH> (no leading zeros, no suffix, not 0.0.0)" >&2
  exit 1
fi
version="${tags#agent-v}"

# 前回より小さい版を配ると、新旧が逆転する。
# パイプで先頭だけ取ると pipefail 下で SIGPIPE になりうるので、変数展開で取る。
all_tags="$(git tag --list 'agent-v*' --sort=-v:refname)"
latest_tag="${all_tags%%$'\n'*}"
if [[ "$tags" != "$latest_tag" ]]; then
  echo "$tags is not the latest version. Use a tag newer than $latest_tag" >&2
  exit 1
fi

# 同梱するバイナリは配る .app のときだけ要る。tauri.conf.json に書かないのは、開発と CI をバイナリ無しで回すため。
npm --prefix ../core ci
npm --prefix ../core run build:bin

# --config は設定にマージされ、package_info().version と Info.plist に入る。
npm run tauri -- build --config src-tauri/tauri.bundle.conf.json --config "{\"version\":\"$version\"}"

rm -rf build/ChatterAgent.app build/ChatterAgent.zip
mkdir -p build
cp -R src-tauri/target/release/bundle/macos/ChatterAgent.app build/
# 拡張属性は zip に入れない。入れると AppleDouble（._*）がバンドルの中に並び、ditto 以外で展開すると署名の封が壊れる。
ditto -c -k --norsrc --keepParent build/ChatterAgent.app build/ChatterAgent.zip
echo "[Build] version=$version → build/ChatterAgent.zip"
