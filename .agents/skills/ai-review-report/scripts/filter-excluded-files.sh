#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG_FILE="${SCRIPT_DIR}/../assets/review-config.json"
CHANGED_FILES="ci_temp/changed_files.txt"
EXCLUDED_FILES="ci_temp/excluded_files.txt"

echo "=========================================="
echo "Filtering excluded files from review"
echo "=========================================="

if [ ! -f "$CONFIG_FILE" ]; then
  echo "No review config found at $CONFIG_FILE - skipping filter"
  exit 0
fi

if [ ! -f "$CHANGED_FILES" ] || [ ! -s "$CHANGED_FILES" ]; then
  echo "No changed files to filter"
  exit 0
fi

PATTERNS=$(jq -r '.excludePatterns[]' "$CONFIG_FILE" 2>/dev/null)

if [ -z "$PATTERNS" ]; then
  echo "No exclude patterns configured - skipping filter"
  exit 0
fi

echo "Exclude patterns:"
echo "$PATTERNS" | while IFS= read -r pattern; do
  echo "  - $pattern"
done

BEFORE_COUNT=$(tr '\0' '\n' < "$CHANGED_FILES" | grep -c . || echo 0)

> "$EXCLUDED_FILES"
> ci_temp/changed_files_filtered.txt

tr '\0' '\n' < "$CHANGED_FILES" | while IFS= read -r file; do
  [ -z "$file" ] && continue
  basename_file=$(basename "$file")
  excluded=false

  while IFS= read -r pattern; do
    [ -z "$pattern" ] && continue
    # shellcheck disable=SC2254
    case "$basename_file" in
      $pattern)
        excluded=true
        break
        ;;
    esac
  done <<< "$PATTERNS"

  if [ "$excluded" = true ]; then
    echo "$file" >> "$EXCLUDED_FILES"
  else
    printf '%s\0' "$file" >> ci_temp/changed_files_filtered.txt
  fi
done

mv ci_temp/changed_files_filtered.txt "$CHANGED_FILES"

AFTER_COUNT=$(tr '\0' '\n' < "$CHANGED_FILES" | grep -c . || echo 0)
EXCLUDED_COUNT=$((BEFORE_COUNT - AFTER_COUNT))

echo ""
echo "Results:"
echo "  Files before: $BEFORE_COUNT"
echo "  Files excluded: $EXCLUDED_COUNT"
echo "  Files remaining: $AFTER_COUNT"

if [ "$EXCLUDED_COUNT" -gt 0 ]; then
  echo ""
  echo "Excluded files:"
  while IFS= read -r file; do
    echo "  - $file"
  done < "$EXCLUDED_FILES"
fi
