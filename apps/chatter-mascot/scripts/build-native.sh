#!/usr/bin/env bash
# macOS の常駐まわり（#75）を担うネイティブプラグインをビルドする。
#
#   ./scripts/build-native.sh
#
# ★ Unity より先に走らせること。 Unity は Assets/Plugins/macOS/ に .bundle が
#   置かれている状態でビルドする必要がある（無くても起動はするが、常駐機能が落ちる）。
#
# ★ 成果物を git にコミットしないこと。 バイナリはレビューできず、
#   plugin/bin/*.mjs のように CI で「ソースと一致するか」を検証する手段が無い
#   （clang の出力に再現性が無い）。.gitignore に入れてある。
set -euo pipefail

PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$PROJECT_PATH/Assets/Plugins/macOS~/ChatterMascotNative"
BUNDLE="$PROJECT_PATH/Assets/Plugins/macOS/ChatterMascotNative.bundle"
BINARY="$BUNDLE/Contents/MacOS/ChatterMascotNative"

if [ ! -d "$SRC" ]; then
  echo "[Native] ソースがありません: $SRC" >&2
  exit 1
fi

mkdir -p "$(dirname "$BINARY")"

# ★ Info.plist を置くこと。 無いと .bundle として認識されず、
#   Unity の PluginImporter が拾わないことがある。
cat > "$BUNDLE/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleExecutable</key><string>ChatterMascotNative</string>
  <key>CFBundleIdentifier</key><string>tech.sukima.chatter-mascot.native</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>ChatterMascotNative</string>
  <key>CFBundlePackageType</key><string>BNDL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
</dict>
</plist>
PLIST

# ★ arm64 と x86_64 の両方を積むこと。 Unity の macOS ビルドは universal で、
#   片方しか無いと Rosetta 環境や Intel Mac で DllNotFoundException になる。
# ★ -mmacosx-version-min は Player Settings の macOSTargetOSVersion に合わせる。
clang -bundle \
  -arch arm64 -arch x86_64 \
  -mmacosx-version-min=12.0 \
  -fobjc-arc \
  -fvisibility=hidden \
  -O2 \
  -Wall -Wextra \
  -framework Cocoa -framework Carbon \
  -o "$BINARY" \
  "$SRC"/*.m

echo "[Native] できました: $BINARY"
echo "[Native] $(lipo -archs "$BINARY")"
