#!/bin/bash
# Get the base branch for the repository (main, master, etc.)
# Usage: get-base-branch.sh

set -e

# Check if main branch exists
if git show-ref --verify --quiet refs/heads/main; then
    echo "main"
elif git show-ref --verify --quiet refs/heads/master; then
    echo "master"
elif git symbolic-ref refs/remotes/origin/HEAD >/dev/null 2>&1; then
    # Try to get default from remote HEAD if it exists
    git symbolic-ref refs/remotes/origin/HEAD | sed 's@^refs/remotes/origin/@@'
else
    # Final fallback
    echo "main"
fi
