#!/usr/bin/env bash
# APK を実機/エミュレータへ入れて起動する（#97）。
#
#   ./scripts/run-android.sh
#   ./scripts/run-android.sh Build/ChatterMascot.apk
#   ./scripts/run-android.sh --no-logcat
#   ./scripts/run-android.sh Build/ChatterMascot.apk --no-logcat
#
# ★ adb はここでは自動検出しない。 ADB 環境変数で上書きできるが、既定は
#   Android Studio 標準の SDK 配置（$HOME/Library/Android/sdk）を見る。
#
# ★ 転送先のポートは CHATTER_AGENT_PORT で選ぶ（既定 8570）。端末側の 8570 は
#   MascotRunner の既定 serverUrl（ws://127.0.0.1:8570）が決め打ちなので変えない。
#   常用サーバーと分けて検証するときは、検証用サーバーに渡したのと同じ値をここにも
#   渡す（1 ランタイムルートに繋ぐクライアントは1台。→ docs/mascot.md「検証時の接続」）。
set -euo pipefail

ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
APP_ID="tech.sukima.chattermascot"
PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# ★ unity.sh は source しない。Unity の存在確認とプロジェクトロックの検査は
#   ここでは要らない（PROJECT_PATH だけを自前で出す）。
APK=""
NO_LOGCAT=""
for arg in "$@"; do
  case "$arg" in
    --no-logcat)
      NO_LOGCAT="--no-logcat"
      ;;
    --*)
      echo "不明なオプションです: $arg" >&2
      exit 1
      ;;
    *)
      if [ -n "$APK" ]; then
        echo "APK は1つだけ指定できます" >&2
        exit 1
      fi
      APK="$arg"
      ;;
  esac
done
APK="${APK:-Build/ChatterMascot.apk}"

# 相対パスは PROJECT_PATH 基準（build-android.sh の出力先解決と同じ形）
case "$APK" in
  /*) ;;
  *) APK="$PROJECT_PATH/$APK" ;;
esac

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
REVERSE_PORT="${CHATTER_AGENT_PORT:-8570}"
"$ADB" reverse tcp:8570 "tcp:$REVERSE_PORT"
echo "転送しました: tcp:8570 -> tcp:$REVERSE_PORT"

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
