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
#   非ループバック（0.0.0.0 など）で待ち受けているときだけ。AP / クライアント分離や別の
#   Wi-Fi・VLAN では mDNS が届かないので、その場合は ws://<ip>:<port> を引数で指定すること。
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
      echo "不明なオプションです: $arg" >&2
      exit 1
      ;;
    *)
      if [ -n "$SERVER_URL" ]; then
        echo "接続先は1つだけ指定できます" >&2
        exit 1
      fi
      SERVER_URL="$arg"
      ;;
  esac
done

if [ -n "$CLEAR" ] && [ -n "$SERVER_URL" ]; then
  echo "--clear と接続先は同時に指定できません" >&2
  exit 1
fi

if [ -z "$CLEAR" ]; then
  if [ -n "$SERVER_URL" ]; then
    case "$SERVER_URL" in
      ws://*|wss://*) ;;
      *)
        echo "接続先は ws:// か wss:// で始まる URL である必要があります: $SERVER_URL" >&2
        exit 1
        ;;
    esac
  fi

  TOKEN_PATH="${XDG_CONFIG_HOME:-$HOME/.config}/chatter-agent/server.token"
  TOKEN="$( { tr -d '[:space:]' < "$TOKEN_PATH"; } 2>/dev/null || true)"
  if [ -z "$TOKEN" ]; then
    echo "先に chatter-agent-server を起動してください（トークンはサーバーの起動時に作られます）" >&2
    exit 1
  fi
fi

if [ ! -x "$ADB" ]; then
  echo "adb が見つかりません: ${ADB}（ADB 環境変数で指定できます）" >&2
  exit 1
fi

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
LOCAL_SETTINGS="$TMP_DIR/settings.json"

if ! "$ADB" pull "$REMOTE_SETTINGS" "$LOCAL_SETTINGS" >/dev/null 2>&1; then
  if [ -n "$CLEAR" ]; then
    echo "settings.json がありません。何もしていません: $REMOTE_SETTINGS"
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
  echo "connection を消しました: $REMOTE_SETTINGS"
else
  echo "書き込みました: $REMOTE_SETTINGS"
  if [ -n "$SERVER_URL" ]; then
    echo "接続先: $SERVER_URL"
  else
    echo "接続先: LAN から自動で探します（mDNS）"
  fi
fi

if [ -n "$NO_RESTART" ]; then
  echo "再起動していません。設定は次回の起動時に読み込まれます（--no-restart）"
else
  "$ADB" shell am force-stop "$APP_ID"
  "$ADB" shell am start -n "$APP_ID/tech.sukima.chattermascot.ChatterMascotGameActivity"
  echo "再起動しました（設定は起動時にだけ読み込まれます）"
fi
