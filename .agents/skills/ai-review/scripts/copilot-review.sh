#!/usr/bin/env bash
# ai-review — deterministic GitHub plumbing for processing a Copilot PR review.
#
# The agent keeps all *judgment* (parsing the review, fix/skip decisions, and the
# text of every reply/summary). This script only performs the fixed GitHub calls so
# the raw REST/GraphQL never has to live in skill prose. Reply/summary bodies are
# read from STDIN to avoid quoting/newline breakage.
#
# Subcommands:
#   detect <pr>                 Print COPILOT if the PR's review or review comments are
#                               authored by the Copilot reviewer bot, else OTHER.
#   threads <pr>                Print the PR's review threads as JSON: each node has
#                               { id, isResolved, comments:[{ databaseId, path, author, body }] }.
#                               The agent maps each parsed issue to a comment databaseId + thread id.
#   reply <pr> <comment-id>     Reply to inline review <comment-id> on <pr>; body read from STDIN.
#   resolve <thread-id>         Mark review thread <thread-id> resolved (GraphQL resolveReviewThread).
#   summary <pr>                Post a PR-level comment (the fix/skip summary table); body from STDIN.
#
# Repo is auto-detected by gh from the current directory ({owner}/{repo} placeholders).
# Usage examples:
#   .../copilot-review.sh detect 48
#   .../copilot-review.sh threads 48
#   echo "**ai-review: FIX** — handled in <sha>" | .../copilot-review.sh reply 48 2101234567
#   .../copilot-review.sh resolve PRRT_kwDOABC123
#   cat summary.md | .../copilot-review.sh summary 48
set -euo pipefail

COPILOT_LOGINS_RE='^(copilot|copilot\[bot\]|copilot-pull-request-reviewer\[bot\])$'

repo_owner() { gh repo view --json owner -q .owner.login; }
repo_name()  { gh repo view --json name  -q .name; }

cmd_detect() {
  local pr="$1"
  # Check both the formal reviews and the inline review comments for a Copilot-bot author.
  local hit
  hit=$(
    {
      gh api --paginate "repos/{owner}/{repo}/pulls/${pr}/reviews" -q '.[].user.login'
      gh api --paginate "repos/{owner}/{repo}/pulls/${pr}/comments" -q '.[].user.login'
    } 2>/dev/null | grep -Ei "$COPILOT_LOGINS_RE" | head -n1 || true
  )
  if [ -n "$hit" ]; then echo "COPILOT"; else echo "OTHER"; fi
}

cmd_threads() {
  local pr="$1"
  local owner repo after page nodes has_next end_cursor tmp next_tmp truncated_comments
  if ! command -v jq >/dev/null 2>&1; then
    echo "ERROR: jq is required for paginated review thread fetching." >&2
    exit 1
  fi
  owner="$(repo_owner)"
  repo="$(repo_name)"
  after=""
  tmp="$(mktemp)"
  next_tmp="$(mktemp)"
  trap 'rm -f "$tmp" "$next_tmp"' EXIT
  printf '[]\n' > "$tmp"

  while :; do
    page=$(gh api graphql \
      -f owner="$owner" -f repo="$repo" -F pr="$pr" -f after="$after" \
      -f query='
      query($owner:String!,$repo:String!,$pr:Int!,$after:String){
        repository(owner:$owner,name:$repo){
          pullRequest(number:$pr){
            reviewThreads(first:100, after:$after){
              pageInfo{ hasNextPage endCursor }
              nodes{
                id isResolved
                comments(first:100){
                  pageInfo{ hasNextPage }
                  nodes{ databaseId path author{ login } body }
                }
              }
            }
          }
        }
      }')

    nodes="$(printf '%s' "$page" | jq -c '.data.repository.pullRequest.reviewThreads.nodes')"
    jq -c --argjson nodes "$nodes" '. + $nodes' "$tmp" > "$next_tmp"
    mv "$next_tmp" "$tmp"
    next_tmp="$(mktemp)"

    truncated_comments="$(printf '%s' "$page" | jq '[.data.repository.pullRequest.reviewThreads.nodes[] | select(.comments.pageInfo.hasNextPage == true)] | length')"
    if [ "${truncated_comments:-0}" -gt 0 ]; then
      echo "WARN: ${truncated_comments} review thread(s) have more than 100 comments; extra replies are omitted from thread JSON." >&2
    fi

    has_next="$(printf '%s' "$page" | jq -r '.data.repository.pullRequest.reviewThreads.pageInfo.hasNextPage')"
    if [ "$has_next" != "true" ]; then
      break
    fi
    end_cursor="$(printf '%s' "$page" | jq -r '.data.repository.pullRequest.reviewThreads.pageInfo.endCursor // ""')"
    if [ -z "$end_cursor" ]; then
      echo "WARN: GitHub reported additional reviewThreads but no endCursor; stopping pagination." >&2
      break
    fi
    after="$end_cursor"
  done

  cat "$tmp"
  rm -f "$tmp" "$next_tmp"
}

cmd_reply() {
  local pr="$1" comment_id="$2"
  gh api -X POST "repos/{owner}/{repo}/pulls/${pr}/comments/${comment_id}/replies" \
    -F body=@- >/dev/null
  echo "REPLIED ${comment_id}"
}

cmd_resolve() {
  local thread_id="$1"
  gh api graphql \
    -f query='mutation($threadId:ID!){ resolveReviewThread(input:{threadId:$threadId}){ thread{ isResolved } } }' \
    -f threadId="$thread_id" \
    -q '.data.resolveReviewThread.thread.isResolved' >/dev/null
  echo "RESOLVED ${thread_id}"
}

cmd_summary() {
  local pr="$1"
  gh api -X POST "repos/{owner}/{repo}/issues/${pr}/comments" \
    -F body=@- -q '.html_url'
}

main() {
  local sub="${1:-}"; shift || true
  case "$sub" in
    detect)  cmd_detect  "$@" ;;
    threads) cmd_threads "$@" ;;
    reply)   cmd_reply   "$@" ;;
    resolve) cmd_resolve "$@" ;;
    summary) cmd_summary "$@" ;;
    *) echo "usage: copilot-review.sh {detect|threads|reply|resolve|summary} ..." >&2; exit 2 ;;
  esac
}

main "$@"
