#!/bin/bash
set -e

# Script: minimize-previous-reviews.sh
# Purpose: Minimize (hide) previous Gemini reviews when a new full review is posted
# Usage: Called from pipeline-code-review-report.yml workflow after posting a full review
# Arguments: $1=PR_NUMBER $2=REVIEW_TYPE $3=GITHUB_REPOSITORY $4=CURRENT_REVIEW_ID (optional)

PR_NUMBER="$1"
REVIEW_TYPE="$2"
GITHUB_REPOSITORY="$3"
CURRENT_REVIEW_ID="${4:-}"

if [ -z "$PR_NUMBER" ] || [ -z "$REVIEW_TYPE" ] || [ -z "$GITHUB_REPOSITORY" ]; then
  echo "Error: Missing required arguments"
  echo "Usage: minimize-previous-reviews.sh PR_NUMBER REVIEW_TYPE GITHUB_REPOSITORY [CURRENT_REVIEW_ID]"
  exit 1
fi

if [ "$REVIEW_TYPE" != "full" ]; then
  echo "Review type is '$REVIEW_TYPE' - skipping minimization (only full reviews trigger this)"
  exit 0
fi

echo "=========================================="
echo "Minimizing Previous AI Reviews"
echo "=========================================="
echo "PR: #${PR_NUMBER}"
echo "Review Type: ${REVIEW_TYPE}"
echo ""

# Extract repository owner and name
REPO_OWNER=$(echo "${GITHUB_REPOSITORY}" | cut -d'/' -f1)
REPO_NAME=$(echo "${GITHUB_REPOSITORY}" | cut -d'/' -f2)

# Get all reviews for the PR via GraphQL (filtered below by review-body marker, not author)
REVIEWS_JSON=$(gh api graphql -f query='
  query($owner: String!, $repo: String!, $pr_number: Int!) {
    repository(owner: $owner, name: $repo) {
      pullRequest(number: $pr_number) {
        reviews(first: 100) {
          nodes {
            id
            databaseId
            body
          }
        }
      }
    }
  }' \
  -f owner="${REPO_OWNER}" \
  -f repo="${REPO_NAME}" \
  -F pr_number="$PR_NUMBER" 2>&1) || {
    echo "⚠️ Failed to fetch PR reviews via GraphQL — skipping minimization (non-fatal)."
    echo "$REVIEWS_JSON"
    exit 0
}

# Extract review Node IDs for Gemini reviews, excluding the current one
GEMINI_REVIEW_NODE_IDS=$(echo "$REVIEWS_JSON" | jq -r \
  --arg current_id "$CURRENT_REVIEW_ID" \
  '.data.repository.pullRequest.reviews.nodes[] |
   select(.body | test("^#+ 🤖 (Gemini CLI|OpenCode CLI) Code Review")) |
   select(if $current_id != "" then (.databaseId | tostring) != $current_id else true end) |
   .id'
)

if [ -z "$GEMINI_REVIEW_NODE_IDS" ]; then
  echo "✅ No previous Gemini reviews found to minimize"
  exit 0
fi

REVIEW_COUNT=$(echo "$GEMINI_REVIEW_NODE_IDS" | wc -l | tr -d ' ')
echo "Found ${REVIEW_COUNT} previous Gemini review(s) to minimize"
echo ""

SUCCESS_COUNT=0
FAIL_COUNT=0

# Minimize each previous Gemini review using GraphQL mutation
while IFS= read -r NODE_ID; do
  if [ -z "$NODE_ID" ]; then
    continue
  fi

  echo "Minimizing review ${NODE_ID}..."

  MUTATION_RESULT=$(gh api graphql -f query='
    mutation($subjectId: ID!, $classifier: ReportedContentClassifiers!) {
      minimizeComment(input: {subjectId: $subjectId, classifier: $classifier}) {
        minimizedComment {
          isMinimized
          minimizedReason
        }
      }
    }' \
    -f subjectId="${NODE_ID}" \
    -f classifier="OUTDATED" 2>&1) || {
    echo "  ❌ Failed to minimize review ${NODE_ID}"
    echo "  Error: $MUTATION_RESULT"
    FAIL_COUNT=$((FAIL_COUNT + 1))
    continue
  }

  # Check if mutation was successful
  IS_MINIMIZED=$(echo "$MUTATION_RESULT" | jq -r '.data.minimizeComment.minimizedComment.isMinimized' 2>/dev/null || echo "false")

  if [ "$IS_MINIMIZED" = "true" ]; then
    echo "  ✅ Successfully minimized review ${NODE_ID}"
    SUCCESS_COUNT=$((SUCCESS_COUNT + 1))
  else
    echo "  ⚠️ Minimize mutation returned but status unclear for ${NODE_ID}"
    echo "  Response: $MUTATION_RESULT"
    FAIL_COUNT=$((FAIL_COUNT + 1))
  fi

  # Small delay to avoid rate limiting
  sleep 0.5
done <<< "$GEMINI_REVIEW_NODE_IDS"

echo ""
echo "=========================================="
echo "Minimization Complete"
echo "=========================================="
echo "✅ Minimized: ${SUCCESS_COUNT}"
if [ "$FAIL_COUNT" -gt 0 ]; then
  echo "⚠️ Failed: ${FAIL_COUNT}"
fi
echo ""

# Exit successfully even if some failed
exit 0
