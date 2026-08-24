#!/usr/bin/env bash
# EditMode テストを走らせる。
#
#   ./scripts/test.sh
#
# 結果は Logs/test-results.xml（NUnit 形式）。
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/unity.sh"

RESULTS="$PROJECT_PATH/Logs/test-results.xml"
mkdir -p "$PROJECT_PATH/Logs"

# ★ -runTests は -quit を付けない（Test Runner が自分で終了する）。
#   付けるとテストが走り切る前に落ちる
set +e
run_unity -runTests -testPlatform EditMode -testResults "$RESULTS" 2>&1 | grep -vE "^\s*$"
STATUS=${PIPESTATUS[0]}
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
