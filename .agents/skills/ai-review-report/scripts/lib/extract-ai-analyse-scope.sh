#!/usr/bin/env bash
# Extract trusted low/medium scope from an OpenCode review body for ai-analyse.
#
# Input: path to a posted gate review body.
# Output: JSON with has_low_medium, medium, low, suggested_fixes, and review_type.
set -euo pipefail

body_file="${1:-}"
if [ -z "$body_file" ] || [ ! -f "$body_file" ]; then
  echo "usage: extract-ai-analyse-scope.sh <review-body-file>" >&2
  exit 64
fi

section_between() {
  local start="$1" end="$2"
  awk -v start="$start" -v end="$end" '
    $0 ~ start { capture = 1; next }
    capture && $0 ~ end { exit }
    capture { print }
  ' "$body_file"
}

has_findings() {
  awk '
    {
      line = $0
      gsub(/^[[:space:]]+|[[:space:]]+$/, "", line)
      if (line == "" || line ~ /^None found[.]?$/) next
      found = 1
    }
    END { exit(found ? 0 : 1) }
  '
}

medium="$(section_between '^### 🟡 Medium Priority Issues' '^### |^## ')"
low="$(section_between '^### 🔵 Low Priority' '^### |^## ')"
suggested_fixes="$(section_between '^## 📝 Suggested Fixes' '^## 🎯 Recommendation')"
review_type="$(sed -n 's/^\*\*Review Type:\*\*[[:space:]]*\([A-Za-z_ -]*\).*$/\1/p' "$body_file" | head -n1)"

has_low_medium=false
if printf '%s\n' "$medium" | has_findings || printf '%s\n' "$low" | has_findings; then
  has_low_medium=true
fi

jq -n \
  --argjson has_low_medium "$has_low_medium" \
  --arg medium "$medium" \
  --arg low "$low" \
  --arg suggested_fixes "$suggested_fixes" \
  --arg review_type "$review_type" \
  '{
    has_low_medium: $has_low_medium,
    medium: $medium,
    low: $low,
    suggested_fixes: $suggested_fixes,
    review_type: $review_type
  }'
