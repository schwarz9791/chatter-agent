#!/usr/bin/env bash
# 配る ChatterAgent（build/ChatterAgent.zip）をビルドする（#191）。
#
#   HEAD に agent-v<MAJOR>.<MINOR>.<PATCH> タグを付けてから、npm run build:release
#
# 更新用の署名鍵（CHATTER_AGENT_UPDATER_KEY）が要る。
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

# updater の署名鍵の関門。chatter-mascot/scripts/build.sh の同名のブロックと同じ形。直すときは両方を揃える。
# パスワードはシェル変数に持ち、署名のコマンドにだけ環境変数で渡す（ビルドの子プロセスには見せない）。
if [[ "$(uname -m)" != "arm64" ]]; then
  echo "Release builds run only on arm64 (latest.json is arm64 only)" >&2
  exit 1
fi
if [[ -z "${CHATTER_AGENT_UPDATER_KEY:-}" ]]; then
  echo "CHATTER_AGENT_UPDATER_KEY is not set. Set it to the absolute path of the updater private key (docs/release.md)" >&2
  exit 1
fi
if [[ ! -f "$CHATTER_AGENT_UPDATER_KEY" ]]; then
  echo "Updater key not found: $CHATTER_AGENT_UPDATER_KEY" >&2
  exit 1
fi
CHATTER_AGENT_UPDATER_KEY="$(/bin/realpath "$CHATTER_AGENT_UPDATER_KEY")"
unset key_pass
key_pass="${TAURI_SIGNING_PRIVATE_KEY_PASSWORD:-}"
unset TAURI_SIGNING_PRIVATE_KEY_PASSWORD TAURI_SIGNING_PRIVATE_KEY TAURI_SIGNING_PRIVATE_KEY_PATH
if [[ -z "$key_pass" ]]; then
  if ! key_pass="$(security find-generic-password -a "$USER" -s chatter-agent-updater-key -w)"; then
    echo "Could not read the chatter-agent-updater-key password from the keychain." \
         "If the item does not exist, register it following docs/release.md, or set TAURI_SIGNING_PRIVATE_KEY_PASSWORD." >&2
    exit 1
  fi
fi

# 同梱するバイナリは配る .app のときだけ要る。tauri.conf.json に書かないのは、開発と CI をバイナリ無しで回すため。
npm --prefix ../core ci
npm --prefix ../core run build:bin

# --config は設定にマージされ、package_info().version と Info.plist に入る。
npm run tauri -- build --config src-tauri/tauri.bundle.conf.json --config "{\"version\":\"$version\"}"

bundle=src-tauri/target/release/bundle/macos
rm -rf build/ChatterAgent.app build/ChatterAgent.zip build/ChatterAgent.app.tar.gz build/ChatterAgent.app.tar.gz.sig build/latest.json
mkdir -p build
cp -R "$bundle/ChatterAgent.app" build/
# 拡張属性は zip に入れない。入れると AppleDouble（._*）がバンドルの中に並び、ditto 以外で展開すると署名の封が壊れる。
ditto -c -k --norsrc --keepParent build/ChatterAgent.app build/ChatterAgent.zip

# 更新の配布物。ChatterAgent は Releases の API でこの名前のアセットを引く。
# ビルドの後で自分で固めて署名する。鍵とパスワードはこの署名のコマンドにしか渡さない。
COPYFILE_DISABLE=1 tar --no-mac-metadata -C build -czf build/ChatterAgent.app.tar.gz ChatterAgent.app
TAURI_SIGNING_PRIVATE_KEY_PASSWORD="$key_pass" node_modules/.bin/tauri signer sign \
  -f "$CHATTER_AGENT_UPDATER_KEY" --app-version "$version" build/ChatterAgent.app.tar.gz
VERSION="$version" SIGNATURE="$(<build/ChatterAgent.app.tar.gz.sig)" node -e '
const v = process.env.VERSION;
const latest = {
  version: v,
  pub_date: new Date().toISOString(),
  platforms: {
    "darwin-aarch64": {
      signature: process.env.SIGNATURE.trim(),
      url: `https://github.com/schwarz9791/chatter-agent/releases/download/agent-v${v}/ChatterAgent.app.tar.gz`,
    },
  },
};
process.stdout.write(JSON.stringify(latest, null, 2) + "\n");
' > build/latest.json
echo "[Build] version=$version → build/ChatterAgent.zip build/ChatterAgent.app.tar.gz build/latest.json"
