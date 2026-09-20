#!/usr/bin/env bash
# EditMode テストを走らせる。
#
#   ./scripts/test.sh
#
# 結果は Logs/test-results.xml（NUnit 形式）。
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

require_unity_cli

RESULTS="$PROJECT_PATH/Logs/test-results.xml"
mkdir -p "$PROJECT_PATH/Logs"
# ★ 前回の結果を消してから走らせること。残したままだと、コンパイルが通らなかったときに
#   古い XML がそのまま集計され、失敗が「total=… passed=… failed=0」として表示される。
#   終了コードは正しく非0になるが、人が読む1行は緑に見える。
rm -f "$RESULTS"

# ★ unity test は -quit を渡さない（Test Runner が自分で終了する）。
#   -- 以降にも -quit を足さないこと（走り切る前に落ちる）。
# ★ -buildTarget OSXUniversal を明示する。build-android.sh の後はアクティブな
#   ビルドターゲットが Android のまま Library に残り、指定しなければ EditMode テストが
#   Android の #if でコンパイルされる。
# ★ 終了コードは unity test 由来。8 はテストの失敗、6 は走り切らなかったことを表す。
set +e
unity test "$PROJECT_PATH" --mode EditMode --output "$RESULTS" "${UNITY_CLI_ARGS[@]}" \
  -- -nographics -buildTarget OSXUniversal
STATUS=$?
set -e

if [ -f "$RESULTS" ]; then
  python3 - "$RESULTS" <<'PY'
import sys, xml.etree.ElementTree as ET
root = ET.parse(sys.argv[1]).getroot()
print()
print(f"total={root.get('total')} passed={root.get('passed')} "
      f"failed={root.get('failed')} skipped={root.get('skipped')} "
      f"duration={root.get('duration')}s")
for case in root.iter("test-case"):
    if case.get("result") == "Failed":
        print(f"\n  FAILED: {case.get('fullname')}")
        for f in case.iter("message"):
            print("    " + (f.text or "").strip().replace("\n", "\n    "))
PY
fi

exit $STATUS
