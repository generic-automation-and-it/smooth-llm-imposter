#!/bin/bash
# Push the current branch to origin, handling upstream tracking and optional rename.
# Usage: push.sh [--rename <new-branch-name>]
#   --rename  rename the local branch first, then push with --set-upstream

set -e

if [ "${1:-}" = "--rename" ]; then
    NEW_BRANCH="${2:?--rename requires a branch name}"
    OLD_BRANCH=$(git rev-parse --abbrev-ref HEAD)
    if [ "$OLD_BRANCH" != "$NEW_BRANCH" ]; then
        git branch -m "$NEW_BRANCH"
        echo "Renamed branch '$OLD_BRANCH' -> '$NEW_BRANCH'"
    fi
    git push --set-upstream origin "$NEW_BRANCH"
    exit 0
fi

BRANCH=$(git rev-parse --abbrev-ref HEAD)

# No upstream configured: push and set upstream
if ! git rev-parse --abbrev-ref @{u} >/dev/null 2>&1; then
    echo "Pushing new branch '$BRANCH' to origin..."
    git push -u origin "$BRANCH"
    exit 0
fi

# Upstream configured: skip when nothing to push
if [ -z "$(git log @{u}..HEAD --oneline)" ]; then
    echo "No commits to push"
    exit 0
fi

git push
