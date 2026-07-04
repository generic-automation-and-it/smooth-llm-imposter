#!/bin/bash
set -euo pipefail

echo "=========================================="
echo "Testing AGENTS.md validation disable flag"
echo "=========================================="
echo ""

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../../" && pwd)"
SOURCE_SCRIPT="${REPO_ROOT}/.agents/skills/ai-review-report/scripts/validate-agents-md.sh"

TMP_DIR="$(mktemp -d /tmp/validate-agents-md-disable.XXXXXX)"
trap 'rm -rf "${TMP_DIR}"' EXIT

FAILURES=0
pass() { echo "PASS: $1"; }
fail() { echo "FAIL: $1"; FAILURES=$((FAILURES + 1)); }

read_validation_passed() {
  local output_file="$1"
  grep '^validation_passed=' "${output_file}" | tail -1 | cut -d'=' -f2
}

run_default_blocks_without_docs() {
  local test_dir="${TMP_DIR}/default"
  local output_file="${TMP_DIR}/default.out"
  mkdir -p "${test_dir}/ci_temp"
  printf 'src/NoDocs.cs\0' > "${test_dir}/ci_temp/changed_files.txt"

  (
    cd "${test_dir}"
    GITHUB_OUTPUT="${output_file}" bash "${SOURCE_SCRIPT}" full >/dev/null
  )

  local validation_passed
  validation_passed="$(read_validation_passed "${output_file}")"
  if [ "${validation_passed}" = "false" ]; then
    pass "default full review still blocks missing documentation"
  else
    fail "expected default validation_passed=false, got '${validation_passed}'"
  fi
}

run_disable_value_passes() {
  local value="$1"
  local label="$2"
  local test_dir="${TMP_DIR}/${label}"
  local output_file="${TMP_DIR}/${label}.out"
  mkdir -p "${test_dir}"

  (
    cd "${test_dir}"
    GITHUB_OUTPUT="${output_file}" \
      OPENCODE_REVIEW_REPORT_DISABLE_AGENTS_MD_CHECK="${value}" \
      bash "${SOURCE_SCRIPT}" full >/dev/null
  )

  local validation_passed
  validation_passed="$(read_validation_passed "${output_file}")"
  if [ "${validation_passed}" = "true" ]; then
    pass "disable value '${value}' bypasses validation"
  else
    fail "expected validation_passed=true for '${value}', got '${validation_passed}'"
  fi
}

run_default_blocks_without_docs
run_disable_value_passes "1" "numeric"
run_disable_value_passes "true" "lower-true"
run_disable_value_passes "TRUE" "upper-true"
run_disable_value_passes "True" "title-true"
run_disable_value_passes "yes" "lower-yes"
run_disable_value_passes "YES" "upper-yes"
run_disable_value_passes "Yes" "title-yes"

if [ "${FAILURES}" -gt 0 ]; then
  echo ""
  echo "${FAILURES} validation disable test(s) failed"
  exit 1
fi

echo ""
echo "=========================================="
echo "AGENTS.md validation disable tests passed"
echo "=========================================="
