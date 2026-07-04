#!/bin/bash
set -euo pipefail

# Regression test for LADR-035 + LADR-036 (PR #5404 false-block class).
# Part 1 — review-in-chunks.sh: a single-directory oversized group must be
#   halved (not survive the adaptive split intact), per-file diffs must be
#   truncated to MAX_FILE_DIFF_SIZE, and no chunk prompt may exceed 250KB —
#   so no chunk ever times out and drops a .failed flag from sheer prompt size.
# Part 2 — aggregate-reviews.sh: with a failed chunk the posted body must carry
#   the coverage banner AND the decision must fail-close to request_changes;
#   without one, the model's APPROVE passes through and no banner appears.
# Modelled on test-review-chunk-threshold.sh: temp git repo, the real scripts,
# stubbed lib/opencode-with-fallback.sh + bin/timeout shim.

echo "=========================================="
echo "Testing chunk prompt budget enforcement"
echo "=========================================="
echo ""

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../../" && pwd)"
SOURCE_CHUNKS_SCRIPT="${REPO_ROOT}/.agents/skills/ai-review-report/scripts/review-in-chunks.sh"
SOURCE_AGG_SCRIPT="${REPO_ROOT}/.agents/skills/ai-review-report/scripts/aggregate-reviews.sh"
SOURCE_BALANCE_LIB="${REPO_ROOT}/.agents/skills/ai-review-report/scripts/lib/balance-fences.sh"

TMP_DIR="$(mktemp -d /tmp/chunk-prompt-budget.XXXXXX)"
trap 'rm -rf "${TMP_DIR}"' EXIT

FAILURES=0
pass() { echo "✅ $1"; }
fail() { echo "❌ $1"; FAILURES=$((FAILURES + 1)); }

setup_repo() {
  local test_repo="${TMP_DIR}/repo"
  mkdir -p "${test_repo}/.agents/skills/ai-review-report/scripts/lib"
  mkdir -p "${test_repo}/bin"

  cp "${SOURCE_CHUNKS_SCRIPT}" "${test_repo}/.agents/skills/ai-review-report/scripts/review-in-chunks.sh"
  cp "${SOURCE_AGG_SCRIPT}" "${test_repo}/.agents/skills/ai-review-report/scripts/aggregate-reviews.sh"
  cp "${SOURCE_BALANCE_LIB}" "${test_repo}/.agents/skills/ai-review-report/scripts/lib/balance-fences.sh"

  # Stub transport: junk for semantic grouping (forces directory-grouping
  # fallback), a clean APPROVE summary for aggregation, >200 bytes of review
  # text for chunk reviews (so no empty-output .failed flag is dropped).
  cat > "${test_repo}/.agents/skills/ai-review-report/scripts/lib/opencode-with-fallback.sh" << 'EOF'
#!/bin/bash
prompt_file="${@: -1}"
case "$prompt_file" in
  *semantic_grouping_prompt.txt)
    echo "semantic grouping unavailable in test"
    ;;
  *summary_prompt.txt)
    cat << 'SUMMARY'
## 📋 Overall Summary
Test summary: this PR was reviewed by the stubbed transport.

### 🔴 Critical Issues
None found

### 🟠 High Priority Issues
None found

### 🟡 Medium Priority Issues
None found

### 🔵 Low Priority / Nitpicks
None found

## 🎯 Recommendation
**Decision:** APPROVE
**Rationale:** Following policy: 0 critical and 0 high priority issues found - approving

**MACHINE_READABLE_ACTION:** approve

---
DETAILED_SECTION_MARKER
---

## 🔄 Holistic Cross-Chunk Analysis
**Overall Assessment:** No significant cross-chunk concerns identified.
SUMMARY
    ;;
  *)
    printf '### Test Review\n\n- 🔵 [VERIFIED] Low Priority: none found in test run.\n\n%.0s' {1..20}
    ;;
esac
EOF
  chmod +x "${test_repo}/.agents/skills/ai-review-report/scripts/lib/opencode-with-fallback.sh"

  cat > "${test_repo}/bin/timeout" << 'EOF'
#!/bin/bash
shift
exec "$@"
EOF
  chmod +x "${test_repo}/bin/timeout"

  cd "${test_repo}"
  git init -q
  git config user.email "test@example.com"
  git config user.name "Test User"

  # Fixture shape mirrors PR #5404: one directory holding the whole oversized
  # group (the deeper-directory regroup is a no-op there) plus one single file
  # whose diff alone dwarfs every cap.
  mkdir -p huge giant
  local i
  for i in $(seq -w 1 12); do
    echo "base" > "huge/f${i}.txt"
  done
  echo "base" > giant/big.txt
  git add huge giant
  git commit -q -m "base"

  # ~21KB diff per huge file → 'huge' group ≈ 255KB (> MAX_CHUNK_SIZE 100KB).
  # Convergence: halve → 2×~128KB → halve → 4×~64KB ≤ 100KB → 4 chunks.
  for i in $(seq -w 1 12); do
    for j in $(seq 1 280); do
      echo "huge content line ${j} for budget testing padding padding padding padding" >> "huge/f${i}.txt"
    done
  done
  # ~1MB single-file diff → unsplittable single-file group → must be TRUNCATED
  # to MAX_FILE_DIFF_SIZE in the prompt instead of timing the chunk out.
  for j in $(seq 1 14000); do
    echo "giant content line ${j} for budget testing padding padding padding padding" >> giant/big.txt
  done
  git add huge giant
  git commit -q -m "head"

  mkdir -p ci_temp
  {
    for i in $(seq -w 1 12); do
      printf 'huge/f%s.txt\0' "${i}"
    done
    printf 'giant/big.txt\0'
  } > ci_temp/changed_files.txt
}

run_budget_case() {
  local test_repo="${TMP_DIR}/repo"
  local output_file="${TMP_DIR}/budget.out"
  local run_log="${TMP_DIR}/budget.log"

  cd "${test_repo}"
  local from_sha to_sha
  from_sha="$(git rev-parse HEAD~1)"
  to_sha="$(git rev-parse HEAD)"

  GITHUB_OUTPUT="${output_file}" \
  PATH="${test_repo}/bin:${PATH}" \
  bash .agents/skills/ai-review-report/scripts/review-in-chunks.sh "${from_sha}" "${to_sha}" "test-model" "test expertise" > "${run_log}" 2>&1

  # 1. 13 files (> single-chunk threshold 10, < semantic threshold 15) →
  #    directory grouping → giant(1) + huge halved twice (4) = 5 chunks.
  local chunks
  chunks="$(grep '^total_chunks=' "${output_file}" | tail -1 | cut -d'=' -f2)"
  if [ "${chunks}" = "5" ]; then
    pass "total_chunks=5"
  else
    fail "expected total_chunks=5, got '${chunks}' (see ${run_log})"
  fi

  # 2. The single-directory group must have taken the halving fallback.
  if grep -q "halving (single directory, cannot split deeper)" "${run_log}"; then
    pass "halving fallback log line present"
  else
    fail "halving fallback log line missing from run log"
  fi

  # 3. Hard ceiling: no chunk prompt may exceed 250,000 bytes.
  local oversized=0 prompt size
  local prompts=()
  shopt -s nullglob
  prompts=(ci_temp/chunk_*_prompt.txt)
  shopt -u nullglob
  if [ "${#prompts[@]}" -eq 0 ]; then
    fail "expected chunk prompt files to be generated"
    return
  fi
  for prompt in "${prompts[@]}"; do
    size="$(wc -c < "${prompt}" | tr -d ' ')"
    if [ "${size}" -gt 250000 ]; then
      echo "   oversized prompt: ${prompt} (${size} bytes)"
      oversized=$((oversized + 1))
    fi
  done
  if [ "${oversized}" -eq 0 ]; then
    pass "all chunk prompts ≤ 250000 bytes"
  else
    fail "${oversized} chunk prompt(s) exceed 250000 bytes"
  fi

  # 4. The giant single-file diff must have been truncated, not appended whole.
  if grep -l "has been TRUNCATED" "${prompts[@]}" > /dev/null 2>&1; then
    pass "truncation marker present in at least one prompt"
  else
    fail "no prompt contains the truncation marker"
  fi

  # 5. Bounded prompts must mean zero failed chunks (the PR #5404 symptom).
  if ls ci_temp/reviews/chunk_*.failed > /dev/null 2>&1; then
    fail "unexpected chunk_*.failed flag file(s) exist"
  else
    pass "no chunk_*.failed flag files"
  fi
}

run_aggregation_case() {
  local with_failed_flag="$1"
  local expected_action="$2"
  local expect_banner="$3"
  local label="$4"
  local test_repo="${TMP_DIR}/repo"
  local output_file="${TMP_DIR}/${label}.out"
  local run_log="${TMP_DIR}/${label}.log"

  cd "${test_repo}"
  rm -rf ci_temp
  mkdir -p ci_temp/reviews
  printf '### 📄 File: `huge/f01.txt`\n\n- 🔵 [VERIFIED] Low Priority: None found\n' > ci_temp/reviews/chunk_0.md
  printf '### 📄 File: `giant/big.txt`\n\n- 🔵 [VERIFIED] Low Priority: None found\n' > ci_temp/reviews/chunk_1.md
  if [ "${with_failed_flag}" = "true" ]; then
    echo "exit code 124" > ci_temp/reviews/chunk_1.failed
  fi

  GITHUB_OUTPUT="${output_file}" \
  bash .agents/skills/ai-review-report/scripts/aggregate-reviews.sh "2" "test-model" "full" "aaaaaaa1234" "2" "bbbbbbb5678" "test expertise" "none" > "${run_log}" 2>&1

  local action
  action="$(grep '^review_action=' "${output_file}" | tail -1 | cut -d'=' -f2)"
  if [ "${action}" = "${expected_action}" ]; then
    pass "${label}: review_action=${action}"
  else
    fail "${label}: expected review_action=${expected_action}, got '${action}' (see ${run_log})"
  fi

  if [ "${expect_banner}" = "true" ]; then
    if grep -q "Review coverage incomplete" ci_temp/final_review.md; then
      pass "${label}: coverage banner present in final_review.md"
    else
      fail "${label}: coverage banner missing from final_review.md"
    fi
  else
    if grep -q "Review coverage incomplete" ci_temp/final_review.md; then
      fail "${label}: coverage banner present despite zero failed chunks"
    else
      pass "${label}: no coverage banner"
    fi
  fi

  # LADR-036 prompt rules must be in the aggregation prompt (full review).
  if [ "${with_failed_flag}" = "true" ]; then
    if grep -q "Review-Coverage Gaps Are NOT Code Issues" ci_temp/summary_prompt.txt; then
      pass "${label}: coverage-gaps block present in summary prompt"
    else
      fail "${label}: coverage-gaps block missing from summary prompt"
    fi
    if grep -q "NOT a missing implementation" ci_temp/summary_prompt.txt; then
      pass "${label}: scoped missing-implementations bullet present"
    else
      fail "${label}: scoped missing-implementations bullet missing"
    fi
  fi
}

setup_repo
run_budget_case
run_aggregation_case "true" "request_changes" "true" "failed-chunk-fail-closed"
run_aggregation_case "false" "approve" "false" "clean-approve-passthrough"

echo ""
if [ "${FAILURES}" -gt 0 ]; then
  echo "=========================================="
  echo "Chunk prompt budget tests FAILED (${FAILURES})"
  echo "=========================================="
  exit 1
fi
echo "=========================================="
echo "Chunk prompt budget tests passed"
echo "=========================================="
