#!/bin/bash
set -e

# Script: validate-agents-md.sh
# Purpose: Validate that FULL reviews include at least one documentation file with proper naming
# Usage: Called from pipeline-code-review-report.yml workflow for FULL reviews only
# Arguments: $1=REVIEW_TYPE
#
# Validation Rules:
#   - FULL reviews must include at least one *AGENTS.md, README.md, or SKILL.md file
#     (added or modified — README.md / SKILL.md match is case-insensitive)
#   - NEW *AGENTS.md files must follow UPPER_SNAKE_CASE naming convention
#   - NEW *AGENTS.md files must be based on template (.agents/templates/TEMPLATE_AGENTS.md)
#   - MODIFIED files are grandfathered (no naming or template validation)
#   - README.md / SKILL.md are not subject to AGENTS.md naming/template rules;
#     they only satisfy the "at least one doc file" gate
#
# Naming Convention (for NEW files only):
#   - Must end with `_AGENTS.md` OR be exactly `AGENTS.md`
#   - Prefix must be UPPER_SNAKE_CASE (uppercase letters, numbers, underscores only)
#   - Numeric prefixes allowed (e.g., 003_SECRETS_AGENTS.md)
#
# Template Requirements (for NEW files only):
#   - Must contain "## 🎯 TL;DR" section (or "## Quick Context")
#
# Valid examples:
#   - AGENTS.md
#   - CACHE_AGENTS.md
#   - PR_GATE_AGENTS.md
#   - 003_SECRETS_AGENTS.md
#   - FEATURE_NAME_AGENTS.md
#
# Invalid examples (for new files):
#   - Testing_Rules_AGENTS.md (mixed case)
#   - tramp-to-liner.AGENTS.md (dot separator, lowercase)
#   - cache_agents.md (lowercase)

REVIEW_TYPE="$1"

echo "=========================================="
echo "AGENTS.md Validation"
echo "Review Type: ${REVIEW_TYPE^^}"
echo "=========================================="
echo ""

DISABLE_AGENTS_MD_CHECK="${OPENCODE_REVIEW_REPORT_DISABLE_AGENTS_MD_CHECK:-0}"
case "${DISABLE_AGENTS_MD_CHECK,,}" in
  1|true|yes)
    echo "Skipping validation - OPENCODE_REVIEW_REPORT_DISABLE_AGENTS_MD_CHECK is enabled"
    echo "validation_passed=true" >> "$GITHUB_OUTPUT"
    echo "validation_message=" >> "$GITHUB_OUTPUT"
    exit 0
    ;;
esac

# Only validate on FULL reviews
if [ "$REVIEW_TYPE" != "full" ]; then
  echo "Skipping validation - only required for FULL reviews"
  echo "validation_passed=true" >> "$GITHUB_OUTPUT"
  echo "validation_message=" >> "$GITHUB_OUTPUT"
  exit 0
fi

# Check if we have changed files
if [ ! -f ci_temp/changed_files.txt ] || [ ! -s ci_temp/changed_files.txt ]; then
  echo "No changed files to analyze"
  echo "validation_passed=false" >> "$GITHUB_OUTPUT"
  echo "validation_message=No files changed in PR" >> "$GITHUB_OUTPUT"
  exit 0
fi

# Check for exempt paths - if ALL changed files are in exempt paths, skip validation
# AGENTS_MD_EXEMPT_PATHS is a newline/space-separated list of path prefixes
if [ -n "$AGENTS_MD_EXEMPT_PATHS" ]; then
  echo "Checking for exempt paths..."

  # Convert exempt paths to array (handles newlines and spaces)
  EXEMPT_PATHS=()
  while IFS= read -r path; do
    # Trim whitespace
    path=$(echo "$path" | xargs)
    if [ -n "$path" ]; then
      EXEMPT_PATHS+=("$path")
    fi
  done <<< "$(echo "$AGENTS_MD_EXEMPT_PATHS" | tr ' ' '\n')"

  echo "Exempt paths configured: ${EXEMPT_PATHS[*]}"

  # Function to check if a file is in an exempt path
  is_in_exempt_path() {
    local file="$1"
    for exempt_path in "${EXEMPT_PATHS[@]}"; do
      if [[ "$exempt_path" == */ ]]; then
        if [[ "$file" == "$exempt_path"* ]]; then
          return 0
        fi
      elif [[ "$file" == "$exempt_path" || "$file" == "$exempt_path/"* ]]; then
        return 0
      fi
    done
    return 1
  }

  # Check if ALL changed files are in exempt paths
  ALL_EXEMPT=true
  NON_EXEMPT_FILES=()
  while IFS= read -r -d '' file; do
    if ! is_in_exempt_path "$file"; then
      ALL_EXEMPT=false
      NON_EXEMPT_FILES+=("$file")
    fi
  done < ci_temp/changed_files.txt

  if [ "$ALL_EXEMPT" = true ]; then
    echo "✅ All changed files are in exempt paths - skipping AGENTS.md validation"
    echo "   Exempt paths: ${EXEMPT_PATHS[*]}"
    echo "validation_passed=true" >> "$GITHUB_OUTPUT"
    echo "validation_message=" >> "$GITHUB_OUTPUT"
    exit 0
  else
    echo "Found ${#NON_EXEMPT_FILES[@]} file(s) not in exempt paths - continuing with validation"
  fi
fi

# Get list of NEW (added) files only - for naming validation.
echo "Identifying new vs modified files..."
NEW_FILES=()

# In CI the workflow has already resolved the correct comparison range
# (merge-base/base SHA -> head SHA). Use that first so this script does not
# depend on a particular remote ref being present as origin/<base>.
BASE_SHA="${BASE_SHA:-}"
HEAD_SHA="${HEAD_SHA:-}"
BASE_REF="${BASE_REF:-main}"

if [ -n "$BASE_SHA" ] && [ -n "$HEAD_SHA" ] \
  && git rev-parse --verify "$BASE_SHA^{commit}" >/dev/null 2>&1 \
  && git rev-parse --verify "$HEAD_SHA^{commit}" >/dev/null 2>&1; then
  git diff --name-only -z --diff-filter=A "${BASE_SHA}..${HEAD_SHA}" -- > ci_temp/new_files_from_range.txt
  while IFS= read -r -d '' file; do
    NEW_FILES+=("$file")
  done < ci_temp/new_files_from_range.txt
elif git diff --cached --name-only --diff-filter=A 2>/dev/null | grep -q .; then
  # Local/testing path where the caller explicitly staged new files.
  git diff --cached --name-only --diff-filter=A 2>/dev/null > ci_temp/new_files_cached.txt
  while IFS= read -r file; do
    NEW_FILES+=("$file")
  done < ci_temp/new_files_cached.txt
elif [ -f ci_temp/feature_branch_files.txt ]; then
  # Last-resort compatibility path for old callers that provide changed-file
  # artifacts but not BASE_SHA/HEAD_SHA. This requires a fetched base ref; if it
  # is absent we warn instead of treating every changed file as new.
  if git rev-parse --verify "origin/${BASE_REF}^{commit}" >/dev/null 2>&1; then
    while IFS= read -r -d '' file; do
      if ! git cat-file -e "origin/${BASE_REF}:$file" 2>/dev/null; then
        NEW_FILES+=("$file")
      fi
    done < ci_temp/changed_files.txt
  else
    echo "⚠️ Base ref origin/${BASE_REF} not available and BASE_SHA/HEAD_SHA not provided; treating AGENTS.md files as modified for naming/template checks."
  fi
elif git rev-parse --verify "origin/${BASE_REF}^{commit}" >/dev/null 2>&1; then
  while IFS= read -r -d '' file; do
    if ! git cat-file -e "origin/${BASE_REF}:$file" 2>/dev/null; then
      NEW_FILES+=("$file")
    fi
  done < ci_temp/changed_files.txt
else
  while IFS= read -r -d '' file; do
    if ! git cat-file -e "HEAD~1:$file" 2>/dev/null; then
      NEW_FILES+=("$file")
    fi
  done < ci_temp/changed_files.txt
fi

# Remove duplicates from NEW_FILES without splitting paths that contain spaces.
if [ "${#NEW_FILES[@]}" -gt 0 ]; then
  printf '%s\n' "${NEW_FILES[@]}" | sort -u > ci_temp/new_files_unique.txt
  NEW_FILES=()
  while IFS= read -r file; do
    NEW_FILES+=("$file")
  done < ci_temp/new_files_unique.txt
fi

echo "New files detected: ${#NEW_FILES[@]}"
for f in "${NEW_FILES[@]}"; do
  echo "  - $f"
done
echo ""

# Function to check if file is newly added in this PR
is_new_file() {
  local file="$1"

  for new_file in "${NEW_FILES[@]}"; do
    if [[ "$new_file" == "$file" ]]; then
      return 0
    fi
  done

  return 1
}

# Function to check if file is in mandatory NFR baseline
is_mandatory_nfr_file() {
  local file="$1"
  local mandatory_files=(
    ".docs/nfr/PROJECT_SETUP_AGENTS.md"
    ".agents/skills/code-review-standards/SKILL.md"
    ".docs/nfr/TOOL_SETUP_AGENTS.md"
    ".agents/rules-scoped/backend/testing-standards.instructions.md"
  )

  for mandatory in "${mandatory_files[@]}"; do
    if [[ "$file" == "$mandatory" ]]; then
      return 0
    fi
  done

  return 1
}

# Function to check template compliance for a file
# Returns 0 if compliant, 1 if not
# Sets MISSING_SECTIONS variable with list of missing sections
check_template_compliance() {
  local file="$1"
  MISSING_SECTIONS=()

  if [ ! -f "$file" ]; then
    MISSING_SECTIONS=("file not found")
    return 1
  fi

  local content
  content=$(cat "$file")

  # Check for TL;DR section (flexible matching)
  # Matches: "## 🎯 TL;DR", "## TL;DR", "## 🎯 Quick Context", "## Quick Context", etc.
  if ! echo "$content" | grep -qE "^##[[:space:]]*(🎯[[:space:]]*)?(TL;DR|Quick Context)"; then
    MISSING_SECTIONS+=("## 🎯 TL;DR")
  fi

  if [ ${#MISSING_SECTIONS[@]} -gt 0 ]; then
    return 1
  fi

  return 0
}

# Function to check structural quality of AGENTS.md content
# Applies quality standards from knowledge-conventional-contexts-quality.instructions.md
# Returns 0 if no issues found, 1 if issues found
# Sets QUALITY_ISSUES_LIST array with list of issues
check_structural_quality() {
  local file="$1"
  QUALITY_ISSUES_LIST=()

  if [ ! -f "$file" ]; then
    return 0
  fi

  local filename
  filename=$(basename "$file")

  # Skip root AGENTS.md and CLAUDE.md (exempt from template requirements)
  if [[ "$filename" == "AGENTS.md" && "$file" == "AGENTS.md" ]] || [[ "$filename" == "CLAUDE.md" && "$file" == "CLAUDE.md" ]]; then
    return 0
  fi

  local content
  content=$(cat "$file")

  # 1. Changelog section must always be present
  if ! echo "$content" | grep -qE "^## (📝 )?Changelog"; then
    QUALITY_ISSUES_LIST+=("Missing required '## Changelog' section")
  fi

  # 2. Anti-pattern: (src: path) annotations
  if echo "$content" | grep -qE '\(src:[[:space:]]'; then
    QUALITY_ISSUES_LIST+=("Contains '(src: path)' annotations (anti-pattern)")
  fi

  # 3. Section ordering validation
  # Sections that exist must appear in this order:
  # TL;DR(1) > Non-Negotiables(2) > System Context(3) > Architecture Decisions(4) >
  # Key Behaviors(5) > Test References(6) > Quality Constraints(7) > Migration Plans(8) > Changelog(9)
  local prev_order=0
  local prev_name=""
  while IFS= read -r line; do
    local order=0
    local name=""
    case "$line" in
      "## TL;DR"*|"## 🎯 TL;DR"*|"## Quick Context"*)
        order=1; name="TL;DR" ;;
      "## Non-Negotiables"*)
        order=2; name="Non-Negotiables" ;;
      "## System Context"*|"## 📋 Overview"*|"## Overview"*)
        order=3; name="System Context/Overview" ;;
      "## Architecture Decisions"*|"## Lightweight ADRs"*)
        order=4; name="Architecture Decisions" ;;
      "## Key Behaviors"*)
        order=5; name="Key Behaviors" ;;
      "## Test References"*)
        order=6; name="Test References" ;;
      "## Quality Constraints"*)
        order=7; name="Quality Constraints" ;;
      "## Migration Plans"*)
        order=8; name="Migration Plans" ;;
      "## Changelog"*|"## 📝 Changelog"*)
        order=9; name="Changelog" ;;
    esac
    if [ "$order" -gt 0 ]; then
      if [ "$order" -lt "$prev_order" ]; then
        QUALITY_ISSUES_LIST+=("Section ordering: '$name' appears after '$prev_name' (must come before)")
      fi
      prev_order="$order"
      prev_name="$name"
    fi
  done <<< "$content"

  # 4. Anti-pattern: Empty sections with N/A content
  if echo "$content" | grep -A1 "^## " | grep -qE "^[[:space:]]*(N/A\.?|None\.?|Not applicable\.?|-|—)[[:space:]]*$"; then
    QUALITY_ISSUES_LIST+=("Contains section(s) with 'N/A' or 'None' (anti-pattern: omit empty sections)")
  fi

  if [ ${#QUALITY_ISSUES_LIST[@]} -gt 0 ]; then
    return 1
  fi

  return 0
}

# Find all *AGENTS.md files (and README.md / SKILL.md, case-insensitive) in the changed files list
echo "Checking for documentation files in PR..."
AGENTS_FILES=()
README_OR_SKILL_FILES=()
INVALID_NAMES=()
INVALID_TEMPLATE=()

while IFS= read -r -d '' file; do
  filename=$(basename "$file")
  filename_lc="${filename,,}"

  # Skip template files (e.g., TEMPLATE_AGENTS.md, TEMPLATE_README.md)
  if [[ "$filename" == TEMPLATE_* ]]; then
    echo "  ⏭️ $file (skipped - template file)"
    continue
  fi

  # Accept README.md or SKILL.md (case-insensitive) as satisfying the doc-file gate.
  # These are not subject to AGENTS.md naming/template validation.
  if [[ "$filename_lc" == "readme.md" ]] || [[ "$filename_lc" == "skill.md" ]]; then
    README_OR_SKILL_FILES+=("$file")
    echo "  ✅ $file (${filename_lc} - accepted as doc file)"
    continue
  fi

  # Check if file ends with AGENTS.md
  if [[ "$filename" == *AGENTS.md ]]; then
    AGENTS_FILES+=("$file")

    # Check if this is a NEW file (needs naming validation) or MODIFIED (grandfathered)
    if ! is_new_file "$file"; then
      # Modified file - grandfathered, no naming or template validation
      echo "  ✅ $file (modified - grandfathered)"
    elif is_mandatory_nfr_file "$file"; then
      # Mandatory NFR baseline file - exempt from naming validation
      # Still check template compliance for new mandatory files
      if check_template_compliance "$file"; then
        echo "  ✅ $file (mandatory NFR baseline - template compliant)"
      else
        echo "  ❌ $file (mandatory NFR baseline - missing required sections: ${MISSING_SECTIONS[*]})"
        INVALID_TEMPLATE+=("$file:${MISSING_SECTIONS[*]}")
      fi
    elif [[ "$filename" == "AGENTS.md" && "$file" == "AGENTS.md" ]]; then
      # Root AGENTS.md file - exempt from template requirements (configuration file)
      echo "  ✅ $file (root AGENTS.md - template exempt)"
    elif [[ "$filename" == "AGENTS.md" ]]; then
      # Non-root AGENTS.md file - check template compliance
      if check_template_compliance "$file"; then
        echo "  ✅ $file (new - base AGENTS.md, template compliant)"
      else
        echo "  ❌ $file (new - missing required sections: ${MISSING_SECTIONS[*]})"
        INVALID_TEMPLATE+=("$file:${MISSING_SECTIONS[*]}")
      fi
    elif [[ "$filename" =~ _AGENTS\.md$ ]]; then
      # Has prefix - extract and validate naming
      prefix="${filename%_AGENTS.md}"

      # Check if prefix is UPPER_SNAKE_CASE (uppercase letters, numbers, underscores only)
      if [[ "$prefix" =~ ^[A-Z0-9_]+$ ]]; then
        # Naming valid - now check template compliance
        if check_template_compliance "$file"; then
          echo "  ✅ $file (new - valid naming, template compliant)"
        else
          echo "  ❌ $file (new - missing required sections: ${MISSING_SECTIONS[*]})"
          INVALID_TEMPLATE+=("$file:${MISSING_SECTIONS[*]}")
        fi
      else
        echo "  ❌ $file (new - invalid naming: prefix '$prefix' must be UPPER_SNAKE_CASE)"
        INVALID_NAMES+=("$file")
      fi
    else
      # Doesn't use underscore separator (e.g., uses dot like tramp-to-liner.AGENTS.md)
      echo "  ❌ $file (new - invalid naming: must use underscore before AGENTS.md)"
      INVALID_NAMES+=("$file")
    fi
  fi
done < ci_temp/changed_files.txt

echo ""

# Structural quality checks on all AGENTS.md files
echo "Running structural quality checks..."
QUALITY_ERRORS_NEW=()
QUALITY_WARNINGS_MODIFIED=()

for file in "${AGENTS_FILES[@]}"; do
  if check_structural_quality "$file"; then
    echo "  ✅ $file (quality check passed)"
  else
    for issue in "${QUALITY_ISSUES_LIST[@]}"; do
      if is_new_file "$file"; then
        QUALITY_ERRORS_NEW+=("$file: $issue")
        echo "  ❌ $file: $issue"
      else
        QUALITY_WARNINGS_MODIFIED+=("$file: $issue")
        echo "  ⚠️  $file: $issue"
      fi
    done
  fi
done
echo ""

# Check results
AGENTS_COUNT=${#AGENTS_FILES[@]}
README_OR_SKILL_COUNT=${#README_OR_SKILL_FILES[@]}
DOC_COUNT=$((AGENTS_COUNT + README_OR_SKILL_COUNT))
INVALID_NAME_COUNT=${#INVALID_NAMES[@]}
INVALID_TEMPLATE_COUNT=${#INVALID_TEMPLATE[@]}
QUALITY_ERROR_COUNT=${#QUALITY_ERRORS_NEW[@]}
QUALITY_WARNING_COUNT=${#QUALITY_WARNINGS_MODIFIED[@]}

echo "Summary:"
echo "  AGENTS.md files found: $AGENTS_COUNT"
echo "  README.md / SKILL.md files found: $README_OR_SKILL_COUNT"
echo "  Total doc files satisfying gate: $DOC_COUNT"
echo "  Invalid naming: $INVALID_NAME_COUNT"
echo "  Missing template sections: $INVALID_TEMPLATE_COUNT"
echo "  Quality issues (new files, blocking): $QUALITY_ERROR_COUNT"
echo "  Quality warnings (modified files, advisory): $QUALITY_WARNING_COUNT"
echo ""

# Build validation message for review comment
# NOTE: This function reads global arrays and their companion count variables.
# The arrays (INVALID_NAMES, INVALID_TEMPLATE, QUALITY_ERRORS_NEW,
# QUALITY_WARNINGS_MODIFIED) must be populated BEFORE calling this function,
# and their count variables (INVALID_NAME_COUNT, INVALID_TEMPLATE_COUNT,
# QUALITY_ERROR_COUNT, QUALITY_WARNING_COUNT) must be kept in sync.
# Adding a new array requires adding both the array population, the count
# increment, and the corresponding branch in this function.
build_validation_message() {
  local message=""

  if [ "$DOC_COUNT" -eq 0 ]; then
    message="## 📋 Missing Documentation File

**FULL reviews require at least one \`*_AGENTS.md\`, \`README.md\`, or \`SKILL.md\` file to be added or modified** (README.md / SKILL.md matched case-insensitively).

This ensures that significant changes are properly documented for AI assistants and developers.

### What to do:
1. Create or update an \`AGENTS.md\`, \`README.md\`, or \`SKILL.md\` file relevant to your changes
2. For new \`*_AGENTS.md\` files: use the template at \`.agents/templates/TEMPLATE_AGENTS.md\` and follow UPPER_SNAKE_CASE naming (\`FEATURE_NAME_AGENTS.md\`)

### Required template sections (new AGENTS.md only):
- \`## 🎯 TL;DR\` - Brief summary of what this documents

### Valid naming examples:
- \`AGENTS.md\` (root/base documentation)
- \`CACHE_AGENTS.md\`
- \`003_SECRETS_AGENTS.md\`
- \`PR_GATE_AGENTS.md\`
- \`README.md\` / \`readme.md\` (any case)
- \`SKILL.md\` / \`skill.md\` (any case)

### Where to place:
- Place in the directory most relevant to your changes
- Parent directories are also valid (context propagates down)
"
  elif [ "$INVALID_NAME_COUNT" -gt 0 ]; then
    message="## 📋 Invalid AGENTS.md Naming Convention

The following **new** AGENTS.md files have invalid naming:

"
    for invalid_file in "${INVALID_NAMES[@]}"; do
      message+="- \`$invalid_file\`
"
    done

    message+="
### Naming Rules (for new files):
- Must end with \`_AGENTS.md\` (underscore separator)
- Prefix must be **UPPER_SNAKE_CASE** (uppercase letters, numbers, underscores only)
- Numeric prefixes allowed (e.g., \`003_SECRETS_AGENTS.md\`)

**Note:** Existing files that are modified are grandfathered and don't need to follow this convention.

### Valid examples:
- \`AGENTS.md\`
- \`CACHE_AGENTS.md\`
- \`003_SECRETS_AGENTS.md\`

### Invalid examples:
- \`Testing_Rules_AGENTS.md\` (mixed case)
- \`tramp-to-liner.AGENTS.md\` (dot separator, lowercase)
"
  elif [ "$INVALID_TEMPLATE_COUNT" -gt 0 ]; then
    message="## 📋 AGENTS.md Missing Required Template Sections

The following **new** AGENTS.md files are missing required sections from the template:

"
    for entry in "${INVALID_TEMPLATE[@]}"; do
      local file_path="${entry%%:*}"
      local missing="${entry#*:}"
      message+="### \`$file_path\`
**Missing sections:** $missing

"
    done

    message+="
### Required Sections (from \`.agents/templates/TEMPLATE_AGENTS.md\`):

New AGENTS.md files must include these sections:

1. **\`## 🎯 TL;DR\`** (or \`## TL;DR\`, \`## Quick Context\`)
   - Brief summary of what this documents
   - Primary value proposition

All other sections (System Context, Non-Negotiables, Key Behaviors, etc.) are optional per \`.agents/rules/knowledge-conventional-contexts-quality.instructions.md\` — include only when they add value.

### How to fix:
1. Copy the template from \`.agents/templates/TEMPLATE_AGENTS.md\`
2. Fill in the required sections
3. Optional sections can be removed if not applicable

**Note:** Existing files that are modified are grandfathered and don't need to follow this convention.
"
  elif [ "$QUALITY_ERROR_COUNT" -gt 0 ]; then
    message="## 📋 AGENTS.md Structural Quality Issues

The following **new** AGENTS.md files have quality issues per \`.agents/rules/knowledge-conventional-contexts-quality.instructions.md\`:

"
    for entry in "${QUALITY_ERRORS_NEW[@]}"; do
      message+="- \`$entry\`
"
    done

    message+="
### Quality Standards:
- **\`## Changelog\` required** — Must be present in all AGENTS.md files, even if empty
- **Section ordering** — When present, sections must follow: TL;DR → Non-Negotiables → System Context → Architecture Decisions → Key Behaviors → Test References → Quality Constraints → Migration Plans → Changelog
- **No \`(src: path)\` annotations** — AI agents can find files themselves
- **No empty sections** — Omit sections entirely instead of filling with 'N/A' or 'None'

See \`.agents/rules/knowledge-conventional-contexts-quality.instructions.md\` for full details.
"
  fi

  # Append quality warnings for modified files (advisory, non-blocking)
  if [ "$QUALITY_WARNING_COUNT" -gt 0 ]; then
    message+="
---

### ⚠️ Quality Warnings (Modified Files — Non-Blocking)

The following modified AGENTS.md files have quality issues. Consider fixing:

"
    for entry in "${QUALITY_WARNINGS_MODIFIED[@]}"; do
      message+="- \`$entry\`
"
    done
  fi

  echo "$message"
}

# Determine validation result
if [ "$DOC_COUNT" -eq 0 ]; then
  echo "❌ VALIDATION FAILED: No AGENTS.md, README.md, or SKILL.md files in PR"
  echo ""
  echo "FULL reviews require at least one *_AGENTS.md, README.md, or SKILL.md file"
  echo "to be added or modified (README.md / SKILL.md matched case-insensitively)."
  echo "For new AGENTS.md files, use the template at .agents/templates/TEMPLATE_AGENTS.md"

  VALIDATION_MESSAGE=$(build_validation_message)

  # Save message to file for workflow to use
  printf '%s\n' "$VALIDATION_MESSAGE" > ci_temp/agents_validation_message.md

  echo "validation_passed=false" >> "$GITHUB_OUTPUT"
  exit 0

elif [ "$INVALID_NAME_COUNT" -gt 0 ]; then
  echo "❌ VALIDATION FAILED: Invalid AGENTS.md naming convention"
  echo ""
  echo "Fix the naming to use UPPER_SNAKE_CASE before _AGENTS.md"

  VALIDATION_MESSAGE=$(build_validation_message)

  # Save message to file for workflow to use
  printf '%s\n' "$VALIDATION_MESSAGE" > ci_temp/agents_validation_message.md

  echo "validation_passed=false" >> "$GITHUB_OUTPUT"
  exit 0

elif [ "$INVALID_TEMPLATE_COUNT" -gt 0 ]; then
  echo "❌ VALIDATION FAILED: AGENTS.md files missing required template sections"
  echo ""
  echo "New AGENTS.md files must include: ## 🎯 TL;DR"
  echo "Use the template at .agents/templates/TEMPLATE_AGENTS.md"

  VALIDATION_MESSAGE=$(build_validation_message)

  # Save message to file for workflow to use
  printf '%s\n' "$VALIDATION_MESSAGE" > ci_temp/agents_validation_message.md

  echo "validation_passed=false" >> "$GITHUB_OUTPUT"
  exit 0

elif [ "$QUALITY_ERROR_COUNT" -gt 0 ]; then
  echo "❌ VALIDATION FAILED: AGENTS.md structural quality issues in new files"
  echo ""
  echo "New AGENTS.md files must follow quality standards in .agents/rules/knowledge-conventional-contexts-quality.instructions.md"

  VALIDATION_MESSAGE=$(build_validation_message)

  # Save message to file for workflow to use
  printf '%s\n' "$VALIDATION_MESSAGE" > ci_temp/agents_validation_message.md

  echo "validation_passed=false" >> "$GITHUB_OUTPUT"
  exit 0

else
  echo "✅ VALIDATION PASSED"
  echo ""
  echo "Found $AGENTS_COUNT valid AGENTS.md file(s) and $README_OR_SKILL_COUNT README.md/SKILL.md file(s) satisfying the doc-file gate"

  # Include quality warnings for modified files if any
  if [ "$QUALITY_WARNING_COUNT" -gt 0 ]; then
    echo ""
    echo "⚠️  Advisory: $QUALITY_WARNING_COUNT quality warning(s) on modified files (non-blocking)"
    VALIDATION_MESSAGE=$(build_validation_message)
    printf '%s\n' "$VALIDATION_MESSAGE" > ci_temp/agents_validation_message.md
  fi

  echo "validation_passed=true" >> "$GITHUB_OUTPUT"
  echo "validation_message=" >> "$GITHUB_OUTPUT"
  exit 0
fi
