#!/bin/bash
set -e

# Get PR metadata from the current branch name, which follows the
# git-policy convention: <type>/<issue>-short-description (issue segment optional).
# Output: JSON {type, issue, slug, branch, pr_title_prefix}
#   type            conventional commit type from the branch prefix ("" if non-conforming)
#   issue           numeric issue from the branch name ("" if none)
#   slug            the short-description segment
#   pr_title_prefix ready-made "<type>[<issue>]" / "<type>[NO-TICKET]" PR-title prefix ("" if type unknown)

BRANCH=$(git branch --show-current)

TYPE=""
ISSUE=""
SLUG=""

if [[ "$BRANCH" =~ ^(feat|fix|chore|docs|refactor|test|ci|perf|build)/(([0-9]+)-)?([^/]+)$ ]]; then
    TYPE="${BASH_REMATCH[1]}"
    ISSUE="${BASH_REMATCH[3]}"
    SLUG="${BASH_REMATCH[4]}"
fi

PREFIX=""
if [ -n "$TYPE" ]; then
    PREFIX="${TYPE}[${ISSUE:-NO-TICKET}]"
fi

jq -n \
    --arg type "$TYPE" \
    --arg issue "$ISSUE" \
    --arg slug "$SLUG" \
    --arg branch "$BRANCH" \
    --arg prefix "$PREFIX" \
    '{type: $type, issue: $issue, slug: $slug, branch: $branch, pr_title_prefix: $prefix}'
