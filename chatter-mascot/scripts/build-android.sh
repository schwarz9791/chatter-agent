#!/usr/bin/env bash
# Android の APK をビルドする（#97）。
#
#   ./scripts/build-android.sh
#   ./scripts/build-android.sh Assets/Scenes/Mascot.unity Build/ChatterMascot.apk
#   CHATTER_MASCOT_KEYSTORE=<keystore の絶対パス> ./scripts/build-android.sh   # release 署名。HEAD に mascot-v<版> タグが要る
#
# ★ AudioManager.asset の trap は無い。 build.sh の trap は BuildScript.BuildMacOS が
#   ビルド中だけ Disable Unity Audio を ON にすることの後始末だが、BuildAndroid は
#   その切り替えを一切行わない（Android は m_DisableAudio: 0 の出荷値のまま鳴らす）ので、
#   戻す対象がそもそも無い。
#
#   trap があるのは release のときだけ。BuildScript は release の版と署名を PlayerSettings へ
#   入れて戻さないので、ProjectSettings.asset を git から戻す。Unity が平文で書き残す署名の
#   パスワード（Library/Bee と Gradle のキャッシュ）も消す。
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

SCENE="${1:-Assets/Scenes/Mascot.unity}"
OUTPUT="${2:-Build/ChatterMascot.apk}"

assert_notice_in_sync

# release は APK とコミットを1対1にするため、作業ツリーがきれいで HEAD にタグが1つだけ付いているときだけ通す。
# 版の規則の検証は BuildScript 側に任せる（規則を2か所に書かない）。
# Unity を起動してからでは遅い失敗（タグ・keystore）は、ここで先に止める。
# タグの確かめ方は chatter-agent-app/scripts/build-release.sh と同じ形。直すときは両方を揃える。
if [[ -n "${CHATTER_MASCOT_KEYSTORE:-}" ]]; then
  if [[ -n "$(git -C "$PROJECT_PATH" status --porcelain)" ]]; then
    echo "作業ツリーに未コミットの変更があります。release ビルドはコミット済みの状態でだけ行います。" >&2
    exit 1
  fi
  tags="$(git -C "$PROJECT_PATH" tag --points-at HEAD --list 'mascot-v*')"
  if [[ -z "$tags" ]]; then
    echo "HEAD に mascot-v* タグがありません。git tag mascot-v<MAJOR>.<MINOR>.<PATCH> を付けてから実行してください（docs/mascot.md「署名と版」）。" >&2
    exit 1
  fi
  if [[ "$tags" == *$'\n'* ]]; then
    echo "HEAD に mascot-v* タグが複数付いています。1つにしてください:" >&2
    echo "$tags" >&2
    exit 1
  fi

  # 前回より小さい版を配ると、端末が上書きを拒んでアンインストール（再ペアリング）になる。
  # パイプで先頭だけ取ると pipefail 下で SIGPIPE になりうるので、変数展開で取る。
  all_tags="$(git -C "$PROJECT_PATH" tag --list 'mascot-v*' --sort=-v:refname)"
  latest_tag="${all_tags%%$'\n'*}"
  if [[ "$tags" != "$latest_tag" ]]; then
    echo "$tags は最新の版ではありません。$latest_tag より大きい版のタグにしてください。" >&2
    exit 1
  fi

  if [[ ! -f "$CHATTER_MASCOT_KEYSTORE" ]]; then
    echo "keystore が見つかりません: $CHATTER_MASCOT_KEYSTORE" >&2
    exit 1
  fi
  CHATTER_MASCOT_KEYSTORE="$(/bin/realpath "$CHATTER_MASCOT_KEYSTORE")"
  export CHATTER_MASCOT_KEYSTORE
  export CHATTER_MASCOT_RELEASE_TAG="$tags"

  # パスワードは argv に出さず、環境変数として Unity へ継承させるだけにする。未指定ならキーチェーンから
  # 読む（→ docs/mascot.md「署名と版」）。読めなければ、下でキャッシュを消す前に止める。
  if [[ -z "${CHATTER_MASCOT_KEYSTORE_PASS:-}" ]]; then
    if ! keystore_pass="$(security find-generic-password -a "$USER" -s chatter-mascot-keystore -w)"; then
      echo "キーチェーンから chatter-mascot-keystore のパスワードを読めませんでした。" \
           "項目が無ければ docs/mascot.md の「署名と版」で登録するか、CHATTER_MASCOT_KEYSTORE_PASS を設定してください。" >&2
      exit 1
    fi
    export CHATTER_MASCOT_KEYSTORE_PASS="$keystore_pass"
    unset keystore_pass
  fi

  # Unity はパスワードを平文で書き残す。書く先は Library/Bee（Gradle の書き出し・Bee の入力データ・ログ）と、
  # Gradle がビルドスクリプトをコンパイルしたキャッシュ（GRADLE_USER_HOME の下）。前者は丸ごと消し、後者は
  # 使い捨ての Gradle ホームに書かせて消す（~/.gradle に残さない。依存は毎回取り直しになる）。
  # 消すのは自分で作ったディレクトリだけで、使う人の GRADLE_USER_HOME には触らない。
  # 作業ツリーがきれいなことを確かめた後なので、ProjectSettings.asset を戻しても手元の変更は消さない。
  # 何度呼ばれても同じ結果になる。
  release_gradle_home=""
  restore_release_settings() {
    git -C "$PROJECT_PATH" checkout -- ProjectSettings/ProjectSettings.asset 2>/dev/null || true
    /bin/rm -rf "$PROJECT_PATH/Library/Bee"
    if [[ -n "$release_gradle_home" ]]; then
      /bin/rm -rf "$release_gradle_home"
    fi
  }
  trap restore_release_settings EXIT INT TERM
  # 複製元などに残った古い Gradle の出力を混ぜないため、release はキャッシュ無しでビルドする
  restore_release_settings
  release_gradle_home="$(mktemp -d -t chatter-mascot-gradle)"
  export GRADLE_USER_HOME="$release_gradle_home"
fi

# ★ Android 専用フラグ（--android-export-type など）を足さないこと。出力形式の書き手を増やすと、
#   honor されなかったときに aab が .apk という名前で出る（→ docs/knowledge/mascot-unity.md「★ Unity CLI」）。
unity_build_player Android ChatterMascot.EditorTools.BuildScript.BuildAndroid \
  "$OUTPUT" Logs/build-android.log "$SCENE"
