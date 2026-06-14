#!/usr/bin/env bash
# git-sync — fetch origin/main and merge into the current branch.
#
# Performs the deterministic part of both modes:
#   - fetches origin/main and merges (non-interactively, --no-edit)
#   - on clean merge:    prints "MERGE_OK" then the last 5 commits
#   - on conflict:       prints "MERGE_CONFLICTS" then the unmerged files,
#                        LEAVING the conflicts in the working tree
#   - on other failure:  prints "MERGE_ERROR" (dirty tree, missing ref, hook
#                        failure, etc. — NOT a content conflict)
#
# Mode 1 (safe): the agent reports the conflicts and stops.
# Mode 2 (--fix): the agent resolves the conflicts left in the tree, stages, commits.
# Either way the merge decision/resolution stays with the agent; this script only
# does the fetch+merge plumbing. Exit code: 0 clean merge, 1 conflict, 2 other error.
#
# Usage: safe-sync.sh [base-ref]   (default base-ref: origin/main)
set -euo pipefail

BASE_REF="${1:-origin/main}"
REMOTE="${BASE_REF%%/*}"
BRANCH="${BASE_REF#*/}"

# Fetch failures (missing remote/ref, network) are non-merge errors — keep the
# MERGE_OK/MERGE_CONFLICTS/MERGE_ERROR contract intact rather than dying via set -e.
if ! git fetch "$REMOTE" "$BRANCH"; then
  echo "MERGE_ERROR"
  exit 2
fi

if git merge --no-edit "$BASE_REF"; then
  echo "MERGE_OK"
  git log --oneline -5
  exit 0
else
  CONFLICTS="$(git diff --name-only --diff-filter=U)"
  if [ -n "$CONFLICTS" ]; then
    echo "MERGE_CONFLICTS"
    printf '%s\n' "$CONFLICTS"
    exit 1
  fi
  # Non-conflict failure: dirty working tree, missing ref, failing hook, etc.
  echo "MERGE_ERROR"
  exit 2
fi
