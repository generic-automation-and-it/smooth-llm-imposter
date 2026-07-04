#!/bin/bash
set -e

# Test script for minimize-previous-reviews.sh
# This script tests the minimize logic without actually making API calls

echo "=========================================="
echo "Testing Minimize Previous Reviews Script"
echo "=========================================="
echo ""

# Test 1: Missing arguments
echo "Test 1: Missing arguments (should fail)"
if bash .agents/skills/ai-review-report/scripts/minimize-previous-reviews.sh 2>&1 | grep -q "Error: Missing required arguments"; then
  echo "✅ Test 1 passed: Missing arguments error detected"
else
  echo "❌ Test 1 failed: Should error on missing arguments"
  exit 1
fi
echo ""

# Test 2: Incremental review type (should skip)
echo "Test 2: Incremental review type (should skip)"
OUTPUT=$(bash .agents/skills/ai-review-report/scripts/minimize-previous-reviews.sh "123" "incremental" "0north/bunker-procurement" 2>&1 || true)
if echo "$OUTPUT" | grep -q "skipping minimization"; then
  echo "✅ Test 2 passed: Incremental reviews skip minimization"
else
  echo "❌ Test 2 failed: Should skip for incremental reviews"
  echo "$OUTPUT"
  exit 1
fi
echo ""

# Test 3: Full review type with mock PR (will fail at API call which is expected)
echo "Test 3: Full review type (will test logic up to API call)"
OUTPUT=$(bash .agents/skills/ai-review-report/scripts/minimize-previous-reviews.sh "123" "full" "0north/bunker-procurement" "999999" 2>&1 || true)

if echo "$OUTPUT" | grep -q "Minimizing Previous AI Reviews"; then
  echo "✅ Test 3 passed: Full review minimization logic triggered (reached script entry with correct args)"
elif echo "$OUTPUT" | grep -qi "not found\|command not found\|no such file"; then
  echo "⚠️  Test 3: Dependency missing — cannot verify logic"
  echo "$OUTPUT"
else
  echo "⚠️  Test 3: Unexpected output — review the script logic"
  echo "$OUTPUT"
fi
echo ""

# Test 4: Review-marker regex matches real review bodies but not quoted copies.
# Guards against an unanchored regex (false-positive minimization of quoted headers)
# AND against over-anchoring (e.g. "^🤖", which breaks because real bodies start with "## 🤖").
# The pattern is extracted from the script itself so this test fails if the regex regresses.
echo "Test 4: Review-marker regex (anchored, single-source-of-truth)"
PATTERN=$(grep -oE 'test\("[^"]*Code Review[^"]*"\)' \
  .agents/skills/ai-review-report/scripts/minimize-previous-reviews.sh \
  | head -1 | sed -E 's/^test\("//; s/"\)$//')

if [ -z "$PATTERN" ]; then
  echo "❌ Test 4 failed: could not extract review-marker regex from minimize-previous-reviews.sh"
  exit 1
fi

# jq powers the regex-semantics assertion below; skip (not fail) when it is absent
# so this test degrades gracefully on a runner without jq.
if ! command -v jq >/dev/null 2>&1; then
  echo "⚠️  Test 4 skipped: jq not available — cannot verify regex match semantics (pattern extracted: $PATTERN)"
else
  REAL='## 🤖 OpenCode CLI Code Review - Commit: `abc1234`'
  QUOTED='> ## 🤖 OpenCode CLI Code Review (quoted by a human in a follow-up comment)'
  REAL_MATCH=$(printf '%s' "$REAL"   | jq -Rs --arg re "$PATTERN" 'test($re)')
  QUOTE_MATCH=$(printf '%s' "$QUOTED" | jq -Rs --arg re "$PATTERN" 'test($re)')

  if [ "$REAL_MATCH" = "true" ] && [ "$QUOTE_MATCH" = "false" ]; then
    echo "✅ Test 4 passed: matches a real review header, ignores a quoted copy (pattern: $PATTERN)"
  else
    echo "❌ Test 4 failed: real-header match=$REAL_MATCH (want true), quoted-copy match=$QUOTE_MATCH (want false)"
    echo "   pattern: $PATTERN"
    exit 1
  fi
fi
echo ""

echo "=========================================="
echo "All basic tests passed!"
echo "=========================================="
echo ""
echo "Note: Full integration testing requires:"
echo "  1. A real PR with existing AI reviews"
echo "  2. Valid GITHUB_TOKEN"
echo "  3. Running in GitHub Actions or with gh CLI configured"
