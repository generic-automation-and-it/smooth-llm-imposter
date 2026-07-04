#!/bin/bash
set -e

# Test script for lib/balance-fences.sh
# Verifies the GFM-aware fence balancing applied to model output before it is
# embedded in the posted review (<details> wrapper protection — PR #36,
# review 4474042824).

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$SCRIPT_DIR/lib/balance-fences.sh"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

echo "=========================================="
echo "Testing balance-fences.sh"
echo "=========================================="
echo ""

pass=0
fail=0

check() {
  local name="$1" expected="$2" actual="$3"
  if [ "$actual" = "$expected" ]; then
    echo "✅ $name"
    pass=$((pass + 1))
  else
    echo "❌ $name"
    echo "--- expected ---"; printf '%s\n' "$expected"
    echo "--- actual ---"; printf '%s\n' "$actual"
    fail=$((fail + 1))
  fi
}

# Test 1: balanced input is unchanged
f="$TMP_DIR/t1.md"
printf 'text\n```yaml\na: 1\n```\nmore text\n' > "$f"
before="$(cat "$f")"
balance_fences "$f"
check "Test 1: balanced input unchanged" "$before" "$(cat "$f")"

# Test 2: open fence at EOF gets closed
f="$TMP_DIR/t2.md"
printf 'text\n```json\n{"a": 1}\n' > "$f"
balance_fences "$f"
check "Test 2: open fence closed at EOF" "$(printf 'text\n```json\n{"a": 1}\n```')" "$(cat "$f")"

# Test 3: the PR #36 nesting pattern — outer ``` wrapping ```yaml. The inner
# bare ``` closes the OUTER block and the intended outer close re-opens, so the
# piece ends with an open fence that must be closed (else it swallows the
# <details> tag appended after this piece).
f="$TMP_DIR/t3.md"
printf 'fix:\n```\n```yaml\na: 1\n```\n```\n## Recommendation\n' > "$f"
balance_fences "$f"
last="$(tail -n 1 "$f")"
check "Test 3: nested-fence parity flip closed at EOF" '```' "$last"

# Test 4: a "closing" fence with an info string does not close (GFM rule)
f="$TMP_DIR/t4.md"
printf '```\ncontent\n```yaml\n' > "$f"
balance_fences "$f"
check "Test 4: info-string fence does not close a block" "$(printf '```\ncontent\n```yaml\n```')" "$(cat "$f")"

# Test 5a: mid-line inline code like ```foo``` never opens a block
f="$TMP_DIR/t5.md"
printf 'see ```inline``` usage\n' > "$f"
before="$(cat "$f")"
balance_fences "$f"
check "Test 5a: inline backtick run is not a fence opener" "$before" "$(cat "$f")"

# Test 5b: a line-start backtick run with backticks in the info string is not a
# fence opener under GFM.
f="$TMP_DIR/t5b.md"
printf '```inline```\ntext\n' > "$f"
before="$(cat "$f")"
balance_fences "$f"
check "Test 5b: line-start backtick info string is not a fence opener" "$before" "$(cat "$f")"

# Test 6: tilde fences supported; longer closing fence closes a shorter opener
f="$TMP_DIR/t6.md"
printf '~~~\ncode\n~~~~\ntext\n' > "$f"
before="$(cat "$f")"
balance_fences "$f"
check "Test 6: tilde fence closed by longer bare fence" "$before" "$(cat "$f")"

# Test 7: 4-space indented fence is literal code, never opens a block
f="$TMP_DIR/t7.md"
printf 'text\n    ```\nstill outside any fence\n' > "$f"
before="$(cat "$f")"
balance_fences "$f"
check "Test 7: 4-space-indented backticks are not a fence" "$before" "$(cat "$f")"

# Test 8: missing file is a no-op (chunk file may be absent)
balance_fences "$TMP_DIR/does-not-exist.md"
check "Test 8: missing file no-op" "0" "$?"

echo ""
echo "=========================================="
echo "Results: $pass passed, $fail failed"
echo "=========================================="
[ "$fail" -eq 0 ]
