#!/usr/bin/env bash
# Android 端末の settings.json に接続先（Mac の chatter-agent-server）を書き込む（#98）。
#
#   ./scripts/configure-android.sh
#   ./scripts/configure-android.sh ws://192.168.1.10:8570
#   ./scripts/configure-android.sh --no-restart
#   ./scripts/configure-android.sh ws://192.168.1.10:8570 --no-restart
#   ./scripts/configure-android.sh --clear
#
# ★ 接続先を省略すると connection.serverUrl を空にしてトークンだけを書く。端末は LAN の
#   `_chatter-agent._tcp`（mDNS）から Mac のサーバーを探して繋ぐ。見つかるのはサーバーが
#   非ループバック（0.0.0.0 など）で待ち受けているときだけ。ルーターがマルチキャストを通さない、
#   別のサブネット・VLAN といった環境では mDNS が届かないので、その場合は ws://<ip>:<port> を
#   引数で指定すること。
#
# ★ トークンは ${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/server.token から読む。
#   chatter-agent-server の起動時に生成される共有シークレットなので、無ければ先にサーバーを
#   起動する。値は標準出力に出さない。
#
# ★ --clear は connection セクションだけを消す。run-android.sh が張る adb reverse の経路
#   （settings.json の connection が空のときだけ使われる）へ戻すためのもの。トークンの
#   読み取りは行わないので、サーバーが止まっていても実行できる。接続先の引数とは
#   同時に指定できない。
#
# ★ adb はここでは自動検出しない。 ADB 環境変数で上書きできるが、既定は
#   Android Studio 標準の SDK 配置（$HOME/Library/Android/sdk）を見る。
#
# ★ 端末側の settings.json は既存キー（audio / character / display など）を
#   保ったまま connection だけ差し替える。音量などの共有キーも同じファイルが持つので、
#   丸ごと書き直すと手で入れた値が消える。
#
# ★ adb reverse には触らない。run-android.sh が張る検証用の踏み台（端末のループバックを
#   Mac のサーバーへ転送する）で、settings.json の connection が空のときの経路として残す。
set -euo pipefail

ADB="${ADB:-$HOME/Library/Android/sdk/platform-tools/adb}"
APP_ID="tech.sukima.chattermascot"
REMOTE_DIR="/sdcard/Android/data/$APP_ID/files"
REMOTE_SETTINGS="$REMOTE_DIR/settings.json"

SERVER_URL=""
NO_RESTART=""
CLEAR=""
for arg in "$@"; do
  case "$arg" in
    --no-restart)
      NO_RESTART="1"
      ;;
    --clear)
      CLEAR="1"
      ;;
    --*)
      echo "Unknown option: $arg" >&2
      exit 1
      ;;
    *)
      if [ -n "$SERVER_URL" ]; then
        echo "Only one server can be specified" >&2
        exit 1
      fi
      SERVER_URL="$arg"
      ;;
  esac
done

if [ -n "$CLEAR" ] && [ -n "$SERVER_URL" ]; then
  echo "--clear and a server cannot be specified together" >&2
  exit 1
fi

if [ -z "$CLEAR" ]; then
  if [ -n "$SERVER_URL" ]; then
    case "$SERVER_URL" in
      ws://*|wss://*) ;;
      *)
        echo "The server must be a URL starting with ws:// or wss://: $SERVER_URL" >&2
        exit 1
        ;;
    esac
  fi

  TOKEN_PATH="${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/server.token"
  TOKEN="$( { tr -d '[:space:]' < "$TOKEN_PATH"; } 2>/dev/null || true)"
  if [ -z "$TOKEN" ]; then
    echo "Start chatter-agent-server first (the token is created when the server starts)" >&2
    exit 1
  fi
fi

if [ ! -x "$ADB" ]; then
  echo "adb not found: ${ADB} (set the ADB environment variable to point at it)" >&2
  exit 1
fi

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
LOCAL_SETTINGS="$TMP_DIR/settings.json"

if ! "$ADB" pull "$REMOTE_SETTINGS" "$LOCAL_SETTINGS" >/dev/null 2>&1; then
  if [ -n "$CLEAR" ]; then
    echo "settings.json does not exist. Nothing to do: $REMOTE_SETTINGS"
    exit 0
  fi
  echo '{"version":1}' > "$LOCAL_SETTINGS"
fi

if [ -n "$CLEAR" ]; then
  # connection が元々無ければ失敗するだけなので無視する（冪等）
  plutil -remove connection "$LOCAL_SETTINGS" >/dev/null 2>&1 || true
else
  # ★ -replace は途中の辞書を作らないので、connection が無いときだけ先に作る
  #   （あれば -insert は失敗するので無視する）。plutil は .json を JSON のまま書き戻す
  plutil -insert connection -json '{}' "$LOCAL_SETTINGS" >/dev/null 2>&1 || true
  # 接続先を省略したときは空文字（＝端末が探す）。値があれば明示の接続先
  plutil -replace connection.serverUrl -string "$SERVER_URL" "$LOCAL_SETTINGS"
  plutil -replace connection.token -string "$TOKEN" "$LOCAL_SETTINGS"
fi

"$ADB" shell mkdir -p "$REMOTE_DIR"
"$ADB" push "$LOCAL_SETTINGS" "$REMOTE_SETTINGS" >/dev/null

if [ -n "$CLEAR" ]; then
  echo "Removed connection from: $REMOTE_SETTINGS"
else
  echo "Wrote: $REMOTE_SETTINGS"
  if [ -n "$SERVER_URL" ]; then
    echo "Server: $SERVER_URL"
  else
    echo "Server: discovered automatically on the LAN (mDNS)"
  fi
fi

if [ -n "$NO_RESTART" ]; then
  echo "Not restarted. The settings are loaded on the next launch (--no-restart)"
else
  "$ADB" shell am force-stop "$APP_ID"
  "$ADB" shell am start -n "$APP_ID/tech.sukima.chattermascot.ChatterMascotGameActivity"
  echo "Restarted (the settings are loaded only at launch)"
fi
