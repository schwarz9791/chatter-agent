#!/usr/bin/env bash
# Unity を batchmode で回す共通部分。
#
# ★ Editor を開いたままだと失敗する。Unity はプロジェクトを排他ロックする。
# ★ batchmode を使う理由は速さではなく、**モーダルダイアログが出ないこと**。
#   Editor 経由（MCP など）でビルドすると、保存確認ダイアログが出た瞬間に応答が返らなくなり、
#   呼び出し側からは「ハングした」としか見えない。
set -euo pipefail

PROJECT_PATH="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if pgrep -f "Unity.app/Contents/MacOS/Unity.*${PROJECT_PATH}" >/dev/null 2>&1; then
  echo "Unity Editor がこのプロジェクトを開いています。閉じてから実行してください" >&2
  exit 1
fi

# ★ Unity CLI を使うスクリプトだけが呼ぶ。source した時点の関門にしないこと ——
#   共通部分（PROJECT_PATH / NOTICE の一致）だけが要るスクリプトを、呼びもしないツールを
#   理由に止めないため。
#
# ★ UNITY_VERSION は CLI の --editor-version へ渡す。渡さないと CLI は
#   ProjectVersion.txt の版で走るので、**指定したつもりの版で走らない**。
#
# ★ --non-interactive を必ず付ける。CLI がプロンプトを出す状態（未ログイン、Editor が
#   未インストール）に入ると、出力は grep の裏・入力は端末のままで無言のハングに見える。
require_unity_cli() {
  if ! command -v unity >/dev/null 2>&1; then
    echo "unity コマンドが見つかりません。次でインストールしてください:" >&2
    echo "  curl -fsSL https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.sh | UNITY_CLI_CHANNEL=beta bash" >&2
    exit 1
  fi

  UNITY_CLI_ARGS=(--no-banner --non-interactive)
  if [ -n "${UNITY_VERSION:-}" ]; then
    UNITY_CLI_ARGS+=(--editor-version "$UNITY_VERSION")
  fi
}

# ★ 出荷物に古いライセンス表記を入れないための関門。ビルドするスクリプトが Unity を呼ぶ前に呼ぶ。
#   同梱コピーは消せない（`.app` からリポジトリの NOTICE は見えず、設定パネルは Editor からも読む）ので、
#   **コピーするのではなく一致を確かめる** —— ビルドが追跡ファイルを書き換える形にすると、
#   中断やクラッシュで食い違ったまま残る経路ができる（→ ProjectSettings/AudioManager.asset の3段構え）。
assert_notice_in_sync() {
  local source="$PROJECT_PATH/../NOTICE"
  local copy="$PROJECT_PATH/Assets/StreamingAssets/NOTICE.txt"
  if ! diff -q "$source" "$copy" >/dev/null; then
    echo "同梱の NOTICE.txt がリポジトリの NOTICE とズレています。次で合わせてから出し直してください:" >&2
    echo "  cp NOTICE chatter-mascot/Assets/StreamingAssets/NOTICE.txt" >&2
    diff -u "$source" "$copy" >&2 || true
    exit 1
  fi
}
