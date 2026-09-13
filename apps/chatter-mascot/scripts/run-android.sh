#!/usr/bin/env bash
# APK を実機/エミュレータへ入れて起動する（#97）。
#
#   ./scripts/run-android.sh
#   ./scripts/run-android.sh Build/ChatterMascot.apk
#   ./scripts/run-android.sh Build/ChatterMascot.apk --no-logcat
#
# ★ adb はここでは自動検出しない。 ADB 環境変数で上書きできるが、既定は
#   Android Studio 標準の SDK 配置（$HOME/Library/Android/sdk）を見る。
set -euo pipefail

ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
APK="${1:-Build/ChatterMascot.apk}"
APP_ID="tech.sukima.chattermascot"
NO_LOGCAT="${2:-}"

if [ ! -x "$ADB" ]; then
  echo "adb が見つかりません: $ADB（ADB 環境変数で指定できます）" >&2
  exit 1
fi

if [ ! -f "$APK" ]; then
  echo "APK がありません: $APK（先に ./scripts/build-android.sh）" >&2
  exit 1
fi

# ★ 端末のループバック 8570 を Mac の chatter-agent-server へ転送する。
#   MascotRunner の既定 serverUrl（ws://127.0.0.1:8570）はそのままに、
#   端末側からは「自分自身に繋いだつもり」で Mac のサーバーへ届く。
#   恒久的な接続先の解決は #98（LAN 経由）で、これは検証用の踏み台。
"$ADB" reverse tcp:8570 tcp:8570

"$ADB" install -r "$APK"

# ★ エントリポイントは GameActivity。 Unity 6 のテンプレートはこの Activity 名で
#   AndroidManifest.xml に登録する。
"$ADB" shell am start -n "$APP_ID/com.unity3d.player.UnityPlayerGameActivity"

if [ "$NO_LOGCAT" = "--no-logcat" ]; then
  echo "起動しました（--no-logcat のため logcat は追いません）"
  exit 0
fi

echo "Unity のログを表示します（Ctrl-C で終了）"
"$ADB" logcat -s Unity
