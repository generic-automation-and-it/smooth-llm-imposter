#!/bin/bash
set -e

# Requires Bash >= 4 (${VAR^^} uppercase expansion). On Bash 3.2 (macOS default)
# this crashes with "bad substitution" — fail fast instead.
if [ "${BASH_VERSINFO:-0}" -lt 4 ]; then
  echo "❌ Requires Bash >= 4 (found ${BASH_VERSION:-unknown}). On macOS: 'brew install bash'." >&2
  exit 1
fi

# Script: aggregate-reviews.sh
# Purpose: Aggregate chunked reviews and generate PR summary
# Usage: Called from pipeline-code-review-report.yml workflow
# Arguments: $1=TOTAL_CHUNKS $2=OPENCODE_MODEL_ID $3=REVIEW_TYPE $4=FROM_SHA $5=FILES_CHANGED $6=CURRENT_SHA $7=EXPERTISE_STATEMENT $8=LAST_FULL_REVIEW_STATUS

TOTAL_CHUNKS="$1"
OPENCODE_MODEL_ID="$2"
REVIEW_TYPE="$3"
FROM_SHA="${4:-unknown}"
FILES_CHANGED="${5:-0}"
CURRENT_SHA="${6:-unknown}"
EXPERTISE_STATEMENT="$7"
LAST_FULL_REVIEW_STATUS="${8:-none}"

if [ -z "$TOTAL_CHUNKS" ] || [ -z "$OPENCODE_MODEL_ID" ] || [ -z "$EXPERTISE_STATEMENT" ]; then
  echo "Error: Missing required arguments"
  echo "Usage: aggregate-reviews.sh TOTAL_CHUNKS OPENCODE_MODEL_ID REVIEW_TYPE [FROM_SHA] [FILES_CHANGED] [CURRENT_SHA] EXPERTISE_STATEMENT [LAST_FULL_REVIEW_STATUS]"
  exit 1
fi

echo "Last full review status: $LAST_FULL_REVIEW_STATUS"

# Fence hygiene: model output may contain nested/unbalanced code fences, which
# flip GFM fence parity and can swallow the <details> wrapper of the posted
# review into a literal code block (PR #36, review 4474042824). Every
# model-generated piece is balanced in place before being embedded.
. "$(dirname "${BASH_SOURCE[0]}")/lib/balance-fences.sh"

# Convert model ID to display name
get_model_display_name() {
  local model_id="$1"
  case "$model_id" in
    gemini-3-pro)
      echo "Gemini 3 Pro"
      ;;
    gemini-3-pro-preview)
      echo "Gemini 3 Pro Preview"
      ;;
    gemini-2.5-pro)
      echo "Gemini 2.5 Pro"
      ;;
    gemini-2.5-pro-preview)
      echo "Gemini 2.5 Pro Preview"
      ;;
    gemini-3-flash-preview)
      echo "Gemini 3 Flash Preview"
      ;;
    gemini-2.5-flash)
      echo "Gemini 2.5 Flash"
      ;;
    *)
      echo "$model_id"
      ;;
  esac
}

get_provider_display_name() {
  case "${OPENCODE_REVIEW_REPORT_PROVIDER:-GEMINI}" in
    GEMINI)
      echo "Google Gemini"
      ;;
    COPILOT)
      echo "GitHub Copilot"
      ;;
    OPENAI)
      echo "OpenAI"
      ;;
    OPENCODE-GO-OPENAI)
      echo "OpenCode Go (OpenAI surface)"
      ;;
    OPENCODE-GO-ANTHROPIC)
      echo "OpenCode Go (Anthropic surface)"
      ;;
    OPEN_ROUTER)
      echo "OpenRouter"
      ;;
    *)
      echo "${OPENCODE_REVIEW_REPORT_PROVIDER}"
      ;;
  esac
}

# $OPENCODE_MODEL_ID is the resolved review model (the chunk-review chain's
# winner). The posted `**Model:**` field shows it — chunk reviews drive the
# substantive findings, so that is the model users care about.
OPENCODE_MODEL_DISPLAY_NAME=$(get_model_display_name "$OPENCODE_MODEL_ID")
OPENCODE_PROVIDER_DISPLAY_NAME=$(get_provider_display_name)

# LADR-022: aggregation / summarisation is not deep analysis — run it on the
# cheap ORCHESTRATOR model, falling back to the resolved review model if the
# orchestrator is down (it is intentionally not probed at startup). The
# orchestrator id is an explicit, independently-tunable env var — no longer
# derived from the review model.
ORCHESTRATOR_MODEL_ID="${OPENCODE_REVIEW_REPORT_MODEL_ORCHESTRATOR:-gemini-3-flash-preview}"

echo "Orchestrator model: $ORCHESTRATOR_MODEL_ID (review model was $OPENCODE_MODEL_ID)"

echo "=========================================="
echo "Aggregating $TOTAL_CHUNKS Chunk Reviews"
echo "=========================================="
echo ""

# Combine all chunk reviews (without header, will be added in final assembly)
cat > ci_temp/combined_reviews.md << 'EOF'
EOF

# Append each chunk review
for i in $(seq 0 $((TOTAL_CHUNKS - 1))); do
  if [ -f ci_temp/reviews/chunk_${i}.md ]; then
    if [ $i -gt 0 ]; then
      echo "---" >> ci_temp/combined_reviews.md
      echo "" >> ci_temp/combined_reviews.md
    fi
    echo "### Chunk #${i}" >> ci_temp/combined_reviews.md
    echo "" >> ci_temp/combined_reviews.md
    # Balanced per chunk so one chunk's open fence cannot corrupt the next
    # chunk, the aggregation prompt, or the posted <details> section.
    balance_fences ci_temp/reviews/chunk_${i}.md
    cat ci_temp/reviews/chunk_${i}.md >> ci_temp/combined_reviews.md
    echo "" >> ci_temp/combined_reviews.md
  else
    echo "⚠️ Warning: Chunk ${i} review file not found"
  fi
done

echo "✅ Combined all chunk reviews"

# LADR-030 (supersedes LADR-017): the holistic / high-level aggregation now runs
# for EVERY PR, including single-chunk ones, so reviewers always get an aggregated
# Overall Summary, Issues Summary, Suggested Fixes and Recommendation — not just the
# raw per-file chunk findings. LADR-017 skipped this for `TOTAL_CHUNKS=1` on the
# premise that the pass was a ~15-min Pro-tier call with no cross-chunk surface; that
# rationale is stale (LADR-022 moved aggregation onto the cheap orchestrator/Flash
# model, ~30 s) and the missing high-level report was the visible gap users hit on
# small PRs. The two safety properties the old short-circuit enforced are preserved
# downstream regardless of chunk count: the fail-closed net (out-of-band
# chunk_<n>.failed flag files, LADR-031) catches any unreviewed chunk, and the
# workflow forces incremental reviews to COMMENT (never APPROVE) per LADR-004.

# Generate PR-level summary
echo "Generating PR summary..."

# Testing rules are now discovered dynamically via *AGENTS.md pattern (Implementation #89)
# No hardcoded path - Testing_Rules_AGENTS.md is found by find-context-files.sh

# Load PR description and extract AI Review Notes section
PR_DESCRIPTION=""
AI_REVIEW_NOTES=""
if [ -f "ci_temp/pr_description.txt" ]; then
  PR_DESCRIPTION=$(cat "ci_temp/pr_description.txt")
  echo "PR description loaded (${#PR_DESCRIPTION} chars)"

  # Extract AI Review Notes section (everything after "## AI Review Notes" header)
  # Uses awk instead of sed to handle case where AI Review Notes is the last section
  if echo "$PR_DESCRIPTION" | grep -q "## AI Review Notes"; then
    AI_REVIEW_NOTES=$(echo "$PR_DESCRIPTION" | awk '/^## AI Review Notes/{flag=1; next} /^## /{flag=0} flag' | sed '/^<!--/,/-->$/d' | sed '/^$/d')
    if [ -n "$AI_REVIEW_NOTES" ]; then
      echo "✅ AI Review Notes extracted for aggregation (${#AI_REVIEW_NOTES} chars)"
    fi
  fi
fi

# LADR-030: aggregation runs for every PR. Phrase the chunk context honestly so a
# single-chunk PR is not described as "multiple chunks".
if [ "$TOTAL_CHUNKS" -eq 1 ]; then
  CHUNK_CONTEXT_INTRO="You are analyzing a pull request whose changes were reviewed in a single chunk. Aggregate that chunk's findings into a clear, high-level PR summary."
else
  CHUNK_CONTEXT_INTRO="You are analyzing a pull request that was reviewed in ${TOTAL_CHUNKS} chunks."
fi

cat > ci_temp/summary_prompt.txt << EOF
${EXPERTISE_STATEMENT}

${CHUNK_CONTEXT_INTRO}

**Review Type:** ${REVIEW_TYPE^^}
EOF

# Add incremental review context if applicable
if [ "$REVIEW_TYPE" = "incremental" ]; then
  cat >> ci_temp/summary_prompt.txt << EOF

## ⚠️ CRITICAL: INCREMENTAL REVIEW LIMITATIONS

**This is an INCREMENTAL review** - you are only seeing CHANGES since the last review, NOT the full PR.

**Current PR Approval Status:** ${LAST_FULL_REVIEW_STATUS^^}
EOF

  # Add status-specific guidance
  if [ "$LAST_FULL_REVIEW_STATUS" = "APPROVED" ]; then
    cat >> ci_temp/summary_prompt.txt << 'EOF'
✅ **This PR has already been APPROVED by a full review.** The incremental review is only checking new changes.
- Do NOT say "a full review is required" or similar - the PR is already approved
- Only flag issues that are NEW in these specific changes
- The approval status should be maintained unless these new changes introduce critical/high issues
EOF
  elif [ "$LAST_FULL_REVIEW_STATUS" = "CHANGES_REQUESTED" ]; then
    cat >> ci_temp/summary_prompt.txt << 'EOF'
⚠️ **This PR has CHANGES_REQUESTED from a previous full review.** Issues may have been addressed in these changes.
- Note if the new changes appear to address previous concerns
- A new full review (/ai-review) is needed to clear the blocking status
EOF
  else
    cat >> ci_temp/summary_prompt.txt << 'EOF'
ℹ️ **No previous full review approval status found.** This may be a new PR or reviews were cleared.
EOF
  fi

  cat >> ci_temp/summary_prompt.txt << 'EOF'

**MANDATORY RULES for incremental reviews:**
1. You CANNOT make holistic claims about "missing implementations" or "missing integration" based on what you see
2. The full PR may have 13 files but you only see changes to 1 file - the other 12 were already reviewed
3. Per LADR-019 the aggregation step does NOT have \`read_file\` — symbol/file verification was already performed during the per-chunk review. Do NOT attempt file reads or claim you have verified anything against the current file state.
4. **NEVER flag "missing integration" as 🟠 High Priority** on incremental reviews — chunk reviews already gated High findings via \`read_file\`. Re-asserting it at aggregation is not adding new signal.
5. Integration concerns on incremental reviews should be 🔵 Low Priority informational notes at most

EOF
fi

# Add AI Review Notes if available
if [ -n "$AI_REVIEW_NOTES" ]; then
  cat >> ci_temp/summary_prompt.txt << EOF

## 📝 AI REVIEW NOTES (from PR author)

The PR author has provided the following guidance for this review:

${AI_REVIEW_NOTES}

**Important:** Consider these notes in your holistic analysis and recommendations.

EOF
fi

cat >> ci_temp/summary_prompt.txt << 'EOF'

**Your task:** Provide TWO sections:
1. A concise PR-level summary (for the main review body)
2. A detailed holistic analysis (to be placed with the individual chunk reviews)

**Important:** This PR's changes were reviewed in one or more chunks for memory efficiency; each chunk was reviewed independently. Your role is to:
1. Aggregate all issues from the chunk review(s)
2. Perform a HOLISTIC analysis looking for cross-cutting concerns, architectural issues, and patterns across the whole PR
3. Surface issues that become apparent when viewing all changes together (for multi-chunk PRs this includes issues that span chunks)

**Confidence Tag Handling:**
- Individual chunk reviews tag findings as `[VERIFIED]` (reviewer saw the code) or `[SPECULATIVE]` (inferred from partial context).
- **Preserve confidence tags** when aggregating issues into the summary. Copy the tag from the chunk review.
- **Do NOT elevate `[SPECULATIVE]` findings** to 🔴 Critical or 🟠 High Priority during aggregation. A speculative finding in a chunk stays speculative in the summary.
- Per LADR-019 you do NOT have file-system access at the aggregation step — chunk reviews already performed `read_file` verification for Critical/High findings. Tag promotion is not your responsibility.

**Review-Coverage Gaps Are NOT Code Issues (MANDATORY):**
- A file that was not included in any review chunk, a chunk that failed or timed out, or a PR-author focus area you could not verify is a REVIEW-COVERAGE GAP, not a code defect.
- **NEVER list a coverage gap under 🔴 Critical, 🟠 High, or 🟡 Medium.** Report it as 🔵 Low Priority informational only, tagged `[SPECULATIVE]` — you have not seen the code, so it can never be `[VERIFIED]`.
- **NEVER count coverage gaps in the Recommendation's Step 1 issue counts.** The pipeline's fail-closed safety net (LADR-031) handles failed chunks mechanically — re-flagging them as blocking issues double-counts the failure.
- This applies even when the PR author's AI Review Notes ask you to focus on that file or area: "I could not verify X" is 🔵 Low, never a blocking finding.

**Required Output Format:**

## 📋 Overall Summary
[2-3 sentences about the PR as a whole - what is being changed and why]

## ✅ Positive Highlights
- [Good practices observed across chunks]
- [Well-written code examples]
- [Good architectural decisions]

## 🔍 Issues Summary

**Note:** Issues are categorized from BOTH individual chunk reviews AND holistic analysis. [📂 View detailed reviews below](#-view-detailed-reviews-click-to-expand)

### 🔴 Critical Issues
[List all critical issues found across ALL chunks AND from holistic analysis, with file references]
[Include cross-chunk issues that only become apparent when viewing the PR holistically]
[If none: "None found"]

### 🟠 High Priority Issues
[List all high priority issues found across ALL chunks AND from holistic analysis, with file references]
[Include integration issues, consistency problems, or architectural concerns]
[If none: "None found"]

### 🟡 Medium Priority Issues
[List medium priority issues or summarize common patterns from chunks AND holistic review]
[If none: "None found"]

### 🔵 Low Priority / Nitpicks
[List low priority issues or summarize common patterns]
[If none: "None found"]

## 📝 Suggested Fixes

**Purpose:** This section consolidates ALL suggested fixes from the individual chunk reviews to make it easy to see what needs to be changed without expanding the detailed reviews.

**Format for each fix:**
```
### `path/to/file.ext:line_number`
**Issue**: [Brief description of the issue] ([Priority emoji and level])
[Code block showing before/after with proper language syntax highlighting]
```

**Instructions:**
- Extract EVERY suggested fix from all chunk reviews below
- Include the file path with line numbers (use the format shown)
- Include the issue description with its priority emoji (🔴 🟠 🟡 🔵)
- Show the code fix with before/after comparison
- Use proper markdown code blocks with language identifiers (csharp, typescript, python, etc.)
- Group related fixes by file if there are multiple fixes for the same file
- Keep fixes in the same order they appear in chunks for easy cross-reference
- If no fixes were suggested in any chunk: write "None - all issues are architectural or require broader discussion"

[Extract and list all suggested fixes from the chunk reviews below]

## 🎯 Recommendation

**CRITICAL POLICY - You MUST follow this decision tree exactly:**

**Step 1: Count ACTUAL issues in your "Issues Summary" section above**
- Count of 🔴 Critical Issues: [number - DO NOT count "None found" as an issue]
- Count of 🟠 High Priority Issues: [number - DO NOT count "None found" as an issue]
- Count of 🟡 Medium Priority Issues: [number - DO NOT count "None found" as an issue]
- Count of 🔵 Low Priority Issues: [number - DO NOT count "None found" as an issue]

**IMPORTANT:** If a section says "None found", the count for that section is 0 (zero). Do NOT count "None found" as an issue.

**Examples:**
- ✅ Correct: 🔴 Critical says "None found" and 🟠 High says "None found" → Critical=0, High=0 → APPROVE
- ❌ Wrong: 🔴 Critical says "None found" but counted as 1 issue → REQUEST_CHANGES

**Step 2: Apply the decision rule (NO EXCEPTIONS):**
- IF (Critical count > 0 OR High Priority count > 0) → **MUST** use REQUEST_CHANGES
- ELSE IF (Medium count > 0 OR Low Priority count > 0) → **MUST** use APPROVE
- ELSE (no issues) → **MUST** use APPROVE

**Step 3: State your decision**

**Decision:** [APPROVE or REQUEST CHANGES]
**Rationale:** [State the rule you followed: "Following policy: [X] critical and [Y] high priority issues found - requesting changes" OR "Following policy: Only [X] medium and [Y] low priority issues found - approving"]

**MACHINE_READABLE_ACTION:** [APPROVE | REQUEST_CHANGES | COMMENT]

**Examples:**
- ✅ Correct: "2 medium issues → APPROVE"
- ✅ Correct: "1 critical issue → REQUEST_CHANGES"
- ❌ Wrong: "1 medium issue that I think is important → REQUEST_CHANGES" (Violates policy)
- ❌ Wrong: "No critical/high issues but many medium → REQUEST_CHANGES" (Violates policy)

---
DETAILED_SECTION_MARKER
---

## 🔄 Holistic Cross-Chunk Analysis
EOF

# Sync mode: narrowed holistic analysis for release branch sync PRs
if [ "${REVIEW_MODE:-standard}" = "sync" ]; then
  cat >> ci_temp/summary_prompt.txt << 'EOF'

**Purpose:** This is a **release branch sync PR**. All code changes were previously reviewed in their original PRs. This analysis focuses ONLY on issues introduced by the merge/sync process itself.

**What we looked for:**
- **Merge conflict resolution errors** — Corrupted code, duplicated blocks, lost changes, or mangled syntax from incorrect conflict resolution
- **Cross-PR breaking combinations** — Changes from separate PRs that are individually correct but incompatible when combined (e.g., removed method still called by another PR's code, conflicting signatures)
- **Configuration/environment drift** — appsettings, feature flags, or env vars that were overridden or lost during the sync
- **Migration ordering conflicts** — EF migrations with conflicting model snapshots or overlapping migration IDs

**Explicitly DO NOT flag:** Coding style, naming, test coverage gaps, performance suggestions, documentation drift, refactoring opportunities, or any issue that would have been caught in the original PR review.

**Severity threshold:** Only use 🔴 Critical and 🟠 High. Classify anything below that as 🔵 Low (informational only). Do NOT use 🟡 Medium for sync reviews.

**Cross-Chunk Issues Found:**

🔴 **Critical Issues**
[List any merge/sync issues. If none: "None found"]

🟠 **High Priority Issues**
[List any cross-PR breaking combinations. If none: "None found"]

🔵 **Low Priority / Informational**
[List any minor observations. If none: "None found"]

**Overall Assessment:** [Brief summary of sync-specific concerns or "No merge/sync issues identified — safe to merge."]
EOF

else
  # Standard/migration/docs-only holistic analysis
  cat >> ci_temp/summary_prompt.txt << 'EOF'

**Purpose:** This analysis views the PR as a unified whole, looking beyond individual chunk reviews for cross-cutting concerns.

**What we looked for:**
- Architectural patterns or anti-patterns across chunks
- Consistency issues between different parts of the codebase
- Breaking changes that affect multiple areas
- Security implications that span multiple files
- Performance impacts when all changes are considered together
- Cross-layer field consistency — entity fields reflected in DTOs, API responses, and frontend models across chunks
- API contract breaking changes — removed/renamed fields, changed response types that could break existing consumers (frontend or external integrations)
EOF

  # Add integration-related checks only for FULL reviews
  if [ "$REVIEW_TYPE" = "full" ]; then
    cat >> ci_temp/summary_prompt.txt << 'EOF'
- **Missing implementations** (e.g., frontend changes without backend support, or vice versa) — based ONLY on the diffs the chunk reviews actually saw. "A file was not present in the review chunks" is a review-coverage gap (🔵 Low, `[SPECULATIVE]`), NOT a missing implementation
- **Integration concerns**: Verify new code is properly called/integrated into the application
- **Dependency Injection**: New classes and interfaces must be properly registered in DI container
- **Test Coverage**: Every code change should have corresponding tests added or updated
- **Concurrency safety**: Patterns where changes across chunks introduce shared state access or parallel execution on the same DbContext/resource (DR-008). Flag as High Priority if multiple chunks show coordinated async patterns without DbContext isolation.
EOF
  else
    cat >> ci_temp/summary_prompt.txt << 'EOF'

**⚠️ INCREMENTAL REVIEW LIMITATION:** This is an incremental review - you only see changes since the last review.
- Do NOT flag "missing integration" or "missing implementation" as High Priority
- Per LADR-019, file-system verification belongs to the chunk-review step, not aggregation. If a chunk review didn't flag it, do not invent it here.
- Integration concerns at the aggregation step are 🔵 Low Priority informational only
EOF
  fi

  cat >> ci_temp/summary_prompt.txt << 'EOF'

**Cross-Chunk Issues Found:**

🔴 **Critical Issues**
[List any critical cross-chunk issues. If none: "None found"]

🟠 **High Priority Issues**
[List any high priority cross-chunk issues. If none: "None found"]

🟡 **Medium Priority Issues**
[List any medium priority cross-chunk issues. If none: "None found"]

🔵 **Low Priority / Nitpicks**
[List any low priority cross-chunk issues. If none: "None found"]

**Additional Analysis:**
- **Consistency:** [Note any consistency issues across chunks]
EOF

  # LADR-020: Skip Integration / DI / Test Coverage sections on small PRs.
  # Per-chunk reviews already evaluate these on the changed files they see.
  # Re-asking the aggregation model to re-derive them on ≤2 chunks is duplicate
  # work — those concerns are intra-chunk, not cross-chunk.
  if [ "$REVIEW_TYPE" = "full" ] && [ "$TOTAL_CHUNKS" -gt 2 ]; then
    cat >> ci_temp/summary_prompt.txt << 'EOF'
- **Integration:** [Describe how chunks integrate together - verify new code is called in startup/entry points]
- **Dependency Injection Analysis**: [List any new classes/interfaces and verify DI registration. If N/A: "Not applicable"]
- **Test Coverage Analysis**: [For each code change, verify corresponding test file exists and was updated. If N/A: "Not applicable"]
  - .NET: Look for *Test.cs, *Tests.cs files matching changed code files
  - Frontend: Look for *.spec.ts files matching changed TypeScript files
  - Python: Look for test_*.py files matching changed Python files
EOF
  fi

  cat >> ci_temp/summary_prompt.txt << 'EOF'

**Overall Assessment:** [Brief summary of cross-chunk concerns or "No significant cross-chunk concerns identified."]
EOF

fi  # end sync/standard branch

cat >> ci_temp/summary_prompt.txt << 'EOF'

---

EOF

# Testing rules are discovered via standard *AGENTS.md pattern (Implementation #89)
# Testing_Rules_AGENTS.md will be included in chunk context if test files are changed

cat >> ci_temp/summary_prompt.txt << 'EOF'

**IMPORTANT - Individual Chunk Reviews for Reference:**

The following individual chunk reviews are provided for your reference to perform the holistic analysis above.
**DO NOT include these chunk reviews in your output** - they will be added separately by the script.
Your output should END after the "Overall Assessment" section above.

---

EOF

cat ci_temp/combined_reviews.md >> ci_temp/summary_prompt.txt

# Call the agent model via opencode for the aggregation summary
# (LADR-022: aggregation runs on the ORCHESTRATOR model, falling back to the
#  resolved review model; LADR-023: opencode transport).
agg_ok=true
bash "$(dirname "${BASH_SOURCE[0]}")/lib/opencode-with-fallback.sh" "$ORCHESTRATOR_MODEL_ID" "$OPENCODE_MODEL_ID" "" -- ci_temp/summary_prompt.txt > ci_temp/pr_summary.md 2>ci_temp/summary_stderr.log || agg_ok=false
# opencode can exit 0 while producing empty/tiny output (silent provider failure).
# Without this, an empty pr_summary.md slips past the success branch and the posted
# review loses its Overall Summary / Issues Summary / Recommendation entirely
# (only "No holistic analysis section found" remains). Treat empty as failure so the
# fail-safe REQUEST_CHANGES fallback below kicks in instead of a blank overview.
agg_size=$(wc -c < ci_temp/pr_summary.md 2>/dev/null || echo 0)
if [ "$agg_ok" = "true" ] && [ "${agg_size:-0}" -lt 50 ]; then
  agg_ok=false
fi
if [ "$agg_ok" = "true" ]; then
  echo "✅ PR summary generated successfully (model: $ORCHESTRATOR_MODEL_ID)"
else
  echo "❌ Summary generation failed/empty - using fallback"
  if [ -s "ci_temp/summary_stderr.log" ]; then
    echo "📋 Stderr log: ci_temp/summary_stderr.log"
  fi
  cat > ci_temp/pr_summary.md << EOF
## 📋 Overall Summary
This PR was reviewed in $TOTAL_CHUNKS chunks. Summary generation encountered an error.
Please review the detailed chunk reviews below.

## 🎯 Recommendation
**Decision:** REQUEST CHANGES (failed to generate summary - review manually)
**Rationale:** Summary generation failed - manual review required for safety

**MACHINE_READABLE_ACTION:** REQUEST_CHANGES
EOF
fi

# Split the summary into main section and detailed section
if grep -q "DETAILED_SECTION_MARKER" ci_temp/pr_summary.md; then
  # Extract main summary (before marker)
  sed '/DETAILED_SECTION_MARKER/,$d' ci_temp/pr_summary.md > ci_temp/pr_summary_main.md

  # Extract detailed holistic analysis (after marker)
  sed -n '/DETAILED_SECTION_MARKER/,$p' ci_temp/pr_summary.md | sed '1,3d' > ci_temp/pr_summary_detailed.md
else
  # Fallback if marker not found (backward compatibility)
  cp ci_temp/pr_summary.md ci_temp/pr_summary_main.md
  echo "## 🔄 Holistic Cross-Chunk Analysis" > ci_temp/pr_summary_detailed.md
  echo "No holistic analysis section found." >> ci_temp/pr_summary_detailed.md
fi

# An open fence at the end of the main summary swallows the <details> tag that
# is appended right after it; one at the end of the detailed section swallows
# the chunk reviews. Balance both halves (the PR #36 breakage was main-side).
balance_fences ci_temp/pr_summary_main.md
balance_fences ci_temp/pr_summary_detailed.md

# Build final review comment with proper structure
# Format SHAs to 7 characters
SHORT_FROM_SHA="${FROM_SHA:0:7}"
SHORT_CURRENT_SHA="${CURRENT_SHA:0:7}"

# LADR-036: count failed chunks BEFORE the body is assembled so the posted body
# can carry a coverage banner that matches the fail-closed override at the end
# of this script. LADR-031: the signal is flag-file existence ONLY — NEVER grep
# review text for the failure marker (a quoted marker false-matched on PR #15).
FAILED_CHUNK_COUNT=$(ls ci_temp/reviews/chunk_*.failed 2>/dev/null | wc -l | tr -d ' ')

cat > ci_temp/final_review.md << EOF
## 🤖 OpenCode CLI Code Review - Commit: \`${SHORT_CURRENT_SHA}\`

\`\`\`
█▀▀█ █▀▀█ █▀▀█ █▀▀▄ █▀▀▀ █▀▀█ █▀▀█ █▀▀█
█░░█ █░░█ █▀▀▀ █░░█ █░░░ █░░█ █░░█ █▀▀▀
▀▀▀▀ █▀▀▀ ▀▀▀▀ ▀  ▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀ ▀▀▀▀
\`\`\`

**Review Type:** ${REVIEW_TYPE^^}
EOF

# Add "Changes Since" for incremental reviews
if [ "$REVIEW_TYPE" = "incremental" ]; then
  cat >> ci_temp/final_review.md << EOF
**Changes Since:** \`${SHORT_FROM_SHA}\`
EOF
fi

cat >> ci_temp/final_review.md << EOF
**Files Changed:** ${FILES_CHANGED}
EOF

if [ -f ci_temp/excluded_files.txt ] && [ -s ci_temp/excluded_files.txt ]; then
  EXCLUDED_COUNT=$(wc -l < ci_temp/excluded_files.txt | tr -d ' ')
  echo "**Files Excluded:** ${EXCLUDED_COUNT} (auto-generated/lock files)" >> ci_temp/final_review.md
fi

cat >> ci_temp/final_review.md << EOF
**Reviewed in:** ${TOTAL_CHUNKS} chunk$([ "$TOTAL_CHUNKS" -ne 1 ] && echo "s" || echo "")
**Model:** ${OPENCODE_MODEL_DISPLAY_NAME}
EOF

# LADR-036: coverage banner. When any chunk failed, the fail-closed override at
# the end of this script forces REQUEST_CHANGES even if the Recommendation says
# APPROVE — say so in the body, so the posted state and the body never
# contradict (review 4465489664 posted an APPROVE-worded body as
# CHANGES_REQUESTED with no explanation).
if [ "${FAILED_CHUNK_COUNT:-0}" -gt 0 ]; then
  cat >> ci_temp/final_review.md << EOF

> ⚠️ **Review coverage incomplete:** ${FAILED_CHUNK_COUNT} of ${TOTAL_CHUNKS} chunk$([ "$TOTAL_CHUNKS" -ne 1 ] && echo "s" || echo "") failed to review (see the failed-chunk details below). Because part of the PR was not reviewed, this review is posted as **REQUEST CHANGES (fail-closed)** regardless of the Recommendation section. Re-run \`/ai-review\` to retry once the failure is addressed.
EOF
fi

cat >> ci_temp/final_review.md << EOF

---

EOF

# Add main summary
cat ci_temp/pr_summary_main.md >> ci_temp/final_review.md

# Add collapsible detailed section
cat >> ci_temp/final_review.md << EOF

---

<details>
<summary><b>📂 View Detailed Reviews</b> (click to expand)</summary>

EOF

# Add holistic analysis with header
cat ci_temp/pr_summary_detailed.md >> ci_temp/final_review.md

echo "" >> ci_temp/final_review.md
echo "---" >> ci_temp/final_review.md
echo "" >> ci_temp/final_review.md

# Add individual chunk reviews with header
cat >> ci_temp/final_review.md << EOF
## 📂 Detailed Chunk Reviews

This PR was reviewed in **$TOTAL_CHUNKS chunk$([ "$TOTAL_CHUNKS" -ne 1 ] && echo "s" || echo "")** to manage memory efficiently.

EOF

cat ci_temp/combined_reviews.md >> ci_temp/final_review.md

# Add AI Review Context Documents section
echo "" >> ci_temp/final_review.md
echo "---" >> ci_temp/final_review.md
echo "" >> ci_temp/final_review.md
echo "## 📚 AI Review Context Documents" >> ci_temp/final_review.md
echo "" >> ci_temp/final_review.md
echo "The following \`*AGENTS.md\` context files were provided to guide this review:" >> ci_temp/final_review.md
echo "" >> ci_temp/final_review.md

# Use all_context_files.txt collected from chunks (Implementation #90)
if [ -f ci_temp/all_context_files.txt ] && [ -s ci_temp/all_context_files.txt ]; then
  while IFS= read -r context_file; do
    echo "- \`${context_file}\`" >> ci_temp/final_review.md
  done < ci_temp/all_context_files.txt
else
  echo "- *No context files found for this PR*" >> ci_temp/final_review.md
fi

echo "" >> ci_temp/final_review.md

cat >> ci_temp/final_review.md << EOF

</details>

---
*Automated review by [opencode](https://opencode.ai) using ${OPENCODE_PROVIDER_DISPLAY_NAME}*
*Model: ${OPENCODE_MODEL_DISPLAY_NAME} | Reviewed in $TOTAL_CHUNKS chunks*
EOF

echo ""
echo "✅ Final review comment prepared"

# Determine review action from summary
# First try to parse the machine-readable action field (more reliable)
REVIEW_DECISION=$(grep -i "^\*\*MACHINE_READABLE_ACTION:\*\*" ci_temp/pr_summary.md \
  | tail -1 \
  | sed -n 's/^.*\*\*MACHINE_READABLE_ACTION:\*\*[[:space:]]*\[\{0,1\}\([A-Za-z_][A-Za-z_]*\)\]\{0,1\}.*$/\1/p' \
  | tr '[:upper:]' '[:lower:]' \
  | tr -d '[:space:]')

# Fail-closed safety net: if ANY chunk failed to review, never APPROVE regardless
# of the summarizer's verdict — a failed chunk means part of the PR was not
# reviewed. This guards every PR, single- or multi-chunk, which is why LADR-030
# could safely drop the old single-chunk short-circuit. The LLM counts
# Critical/High findings and would otherwise treat a failure as "0 issues".
#
# LADR-031: the signal is OUT-OF-BAND — `review-in-chunks.sh` drops a
# `ci_temp/reviews/chunk_<n>.failed` flag file for any chunk it could not review.
# We do NOT grep the review TEXT for "## ⚠️ Review Failed": when this gate reviews
# its own repo, the review body legitimately QUOTES that marker (it's documented in
# SKILL.md and this script), and a text grep false-matched the quote → forced
# REQUEST_CHANGES on a clean APPROVE (observed on PR #15). A flag file cannot be
# quoted into existence by review content.
# LADR-036: FAILED_CHUNK_COUNT was computed from the same flag files before the
# body was assembled, so the coverage banner above and this override always agree.
if [ "${FAILED_CHUNK_COUNT:-0}" -gt 0 ]; then
  if [ "$REVIEW_DECISION" != "request_changes" ]; then
    echo "⚠️ ${FAILED_CHUNK_COUNT} chunk(s) failed to review — forcing REQUEST_CHANGES (fail-closed), overriding '${REVIEW_DECISION:-unknown}'."
    REVIEW_DECISION="request_changes"
  fi
fi

if [ "$REVIEW_DECISION" = "request_changes" ]; then
  echo "review_action=request_changes" >> "$GITHUB_OUTPUT"
  echo "📋 Recommendation: REQUEST CHANGES (from machine-readable field)"
elif [ "$REVIEW_DECISION" = "approve" ]; then
  echo "review_action=approve" >> "$GITHUB_OUTPUT"
  echo "📋 Recommendation: APPROVE (from machine-readable field)"
elif [ "$REVIEW_DECISION" = "comment" ]; then
  echo "review_action=comment" >> "$GITHUB_OUTPUT"
  echo "📋 Recommendation: COMMENT (from machine-readable field)"
else
  # Fallback to parsing text/emojis if machine-readable field isn't present or is unclear
  echo "⚠️ Machine-readable action not found or unclear, falling back to text parsing"
  if grep -qi "REQUEST CHANGES" ci_temp/pr_summary.md; then
    echo "review_action=request_changes" >> "$GITHUB_OUTPUT"
    echo "📋 Recommendation: REQUEST CHANGES (from text parsing)"
  else
    # Check if there are ACTUAL critical/high issues (not just "None found" placeholders).
    # Extract the FULL body of each severity section (heading → next markdown heading),
    # not just the 2 trailing lines `grep -A2` would catch: a section can list several
    # multi-line issues separated by blank lines, which -A2 would silently undercount.
    # Tertiary fallback only — the machine-readable field and "REQUEST CHANGES" text
    # parse run first; issue/content lines never start with '#', so a '#'-prefixed line
    # is unambiguously the next heading and terminates the section.
    _section_body() { awk -v h="$1" 'index($0,h){g=1;next} g&&/^#/{g=0} g' ci_temp/pr_summary.md 2>/dev/null; }
    CRITICAL_ISSUES=$(_section_body "### 🔴 Critical Issues" | grep -vi "None found" | grep -v "^[[:space:]]*$" || true)
    HIGH_ISSUES=$(_section_body "### 🟠 High Priority Issues" | grep -vi "None found" | grep -v "^[[:space:]]*$" || true)
    if [ -n "$CRITICAL_ISSUES" ] || [ -n "$HIGH_ISSUES" ]; then
      echo "review_action=request_changes" >> "$GITHUB_OUTPUT"
      echo "📋 Recommendation: REQUEST CHANGES (critical/high issues found via content parsing)"
    elif grep -qiE "(decision|recommendation|machine_readable_action).*approve" ci_temp/pr_summary.md; then
      echo "review_action=approve" >> "$GITHUB_OUTPUT"
      echo "📋 Recommendation: APPROVE (from text parsing)"
    else
      echo "review_action=comment" >> "$GITHUB_OUTPUT"
      echo "📋 Recommendation: COMMENT (unclear from summary)"
    fi
  fi
fi

echo ""
echo "=========================================="
echo "Aggregation Complete"
echo "=========================================="
