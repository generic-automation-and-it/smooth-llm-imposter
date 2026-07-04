# Minimize Previous Reviews

## Overview

This feature automatically minimizes (marks as "OUTDATED") previous PR code reviews when a new **full review** is posted on a Pull Request. This keeps the PR conversation clean and focused on the latest comprehensive review.

## How It Works

### Workflow Integration

The `minimize-previous-reviews.sh` script is integrated into the `.github/workflows/pipeline-code-review-report.yml` workflow:

1. **After posting a full review**, the workflow automatically calls the minimize script
2. **Only full reviews trigger minimization** - incremental reviews are left visible
3. **The current review is excluded** - only previous reviews are minimized

### Script Behavior

The `minimize-previous-reviews.sh` script:

1. ✅ Fetches all reviews for the PR using GitHub API
2. ✅ Filters for posted review titles (`🤖 … Code Review`) — matches both the current "OpenCode CLI" and legacy "Gemini CLI" titles for backward compatibility
3. ✅ Excludes the current review ID (if provided)
4. ✅ Sets up GitHub session with cookies and CSRF token
5. ✅ Makes POST requests to minimize each previous review
6. ✅ Marks them as "OUTDATED" in the GitHub UI

## Usage

### Automatic (CI/CD)

The script runs automatically when:
- A full review is posted via workflow
- Triggered by `/ai-review` comment
- Triggered by re-requesting review
- Triggered by manual workflow dispatch

### Manual Testing

To test the script locally:

```bash
# Run basic validation tests
.agents/skills/ai-review-report/scripts/test-minimize-reviews.sh

# Test with a real PR (requires GITHUB_TOKEN)
.agents/skills/ai-review-report/scripts/minimize-previous-reviews.sh \
  <PR_NUMBER> \
  "full" \
  "0north/bunker-procurement" \
  [CURRENT_REVIEW_ID]
```

## Script Arguments

| Argument | Required | Description |
|----------|----------|-------------|
| `PR_NUMBER` | ✅ | The pull request number |
| `REVIEW_TYPE` | ✅ | Review type: "full" or "incremental" |
| `GITHUB_REPOSITORY` | ✅ | Repository in format "owner/repo" |
| `CURRENT_REVIEW_ID` | ⚠️ | (Optional) ID of current review to exclude |

## Environment Variables

| Variable | Required | Description |
|----------|----------|-------------|
| `GITHUB_TOKEN` | ✅ | GitHub authentication token |

## API Endpoint Used

The script uses the **official GitHub GraphQL API**:

### Query: Fetch Reviews
```graphql
query($owner: String!, $repo: String!, $pr_number: Int!) {
  repository(owner: $owner, name: $repo) {
    pullRequest(number: $pr_number) {
      reviews(first: 100) {
        nodes {
          id            # Node ID (required for mutation)
          databaseId    # Database ID (for filtering)
          body          # Review content
          author { login }
        }
      }
    }
  }
}
```

### Mutation: Minimize Comment
```graphql
mutation($subjectId: ID!, $classifier: ReportedContentClassifiers!) {
  minimizeComment(input: {subjectId: $subjectId, classifier: $classifier}) {
    minimizedComment {
      isMinimized
      minimizedReason
    }
  }
}
```

**Parameters:**
- `subjectId`: The Node ID of the review (starts with "PRR_")
- `classifier`: "OUTDATED" - marks review as outdated

## Why Minimize Reviews?

### Benefits

1. **Cleaner PR Conversation** - Old reviews are collapsed automatically
2. **Focus on Latest Review** - Most recent full review is prominent
3. **Historical Context Preserved** - Old reviews are still accessible when expanded
4. **Better UX** - Reduces scrolling through outdated feedback

### When Minimization Occurs

| Scenario | Minimizes Previous Reviews? |
|----------|---------------------------|
| Full review posted | ✅ Yes |
| Incremental review posted | ❌ No |
| `/ai-review` comment | ✅ Yes (triggers full review) |
| Re-request review | ✅ Yes (triggers full review) |
| Manual workflow dispatch | ✅ Yes (triggers full review) |
| Auto review on PR update | ❌ No (triggers incremental review) |

## Error Handling

The script is designed to be resilient:

- ✅ Exits gracefully if no previous reviews found
- ✅ Continues on individual failure (doesn't abort all)
- ✅ Reports success/failure counts
- ✅ Never fails the entire workflow
- ✅ Provides detailed logging for debugging

## Example Output

```
==========================================
Minimizing Previous Gemini Reviews
==========================================
PR: #3946
Review Type: full

Found 3 previous Gemini review(s) to minimize

Minimizing review PRR_kwDOC1N...
  ✅ Successfully minimized review PRR_kwDOC1N...
Minimizing review PRR_kwDOC2M...
  ✅ Successfully minimized review PRR_kwDOC2M...
Minimizing review PRR_kwDOC3L...
  ✅ Successfully minimized review PRR_kwDOC3L...

==========================================
Minimization Complete
==========================================
✅ Minimized: 3
```

## Troubleshooting

### Issue: Reviews not minimizing

**Check:**
1. Is `GITHUB_TOKEN` properly set?
2. Does the token have `pull-requests: write` permission?
3. Are you running with `review_type=full`?
4. Verify GraphQL API access: `gh api graphql -f query='{viewer{login}}'`

### Issue: GraphQL errors

**Common errors:**
- `Resource not accessible by integration` - Token lacks required permissions
- `Could not resolve to a node` - Invalid Node ID format
- `NOT_FOUND` - Review may have been deleted

**Solution:**
Check the error message in the workflow logs for specific details.

### Issue: Rate limiting

**Solution:**
The script includes a 0.5 second delay between requests to avoid rate limits. GraphQL API has different rate limits than REST API (5,000 points/hour vs 5,000 requests/hour).

## Related Files

- **Script**: `.agents/skills/ai-review-report/scripts/minimize-previous-reviews.sh`
- **Workflow**: `.github/workflows/pipeline-code-review-report.yml`
- **Test Script**: `.agents/skills/ai-review-report/scripts/test-minimize-reviews.sh`

## References

Based on the GitHub web UI minimize functionality:
- Endpoint: `/pull/{pr}/reviews/{review_id}/minimize`
- Classifier: `OUTDATED`
- Method: POST (with `_method=put` override)
