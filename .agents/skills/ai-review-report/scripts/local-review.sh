#!/bin/bash
set -e

# Requires Bash >= 4 (associative arrays, mapfile, ${VAR^^}, wait -n throttling).
# macOS ships Bash 3.2 by default — without this guard those constructs crash or
# silently no-op (e.g. unthrottled gateway requests). Run with a newer bash.
if [ "${BASH_VERSINFO:-0}" -lt 4 ]; then
  echo "❌ Requires Bash >= 4 (found ${BASH_VERSION:-unknown}). On macOS: 'brew install bash' and run it (e.g. /opt/homebrew/bin/bash $0)." >&2
  exit 1
fi

# Script: local-review.sh
# Purpose: Run Gemini code review locally by wrapping the existing CI scripts
# Usage:
#   local-review.sh                          # Review current branch vs main
#   local-review.sh --pr 1234                # Review PR #1234
#   local-review.sh --base develop           # Review against a different base branch
#   local-review.sh --model gemini-3-pro     # Use a specific model
#   local-review.sh --post                   # Post review to PR (requires --pr)
#   local-review.sh --open                   # Open final review in $EDITOR after completion

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../../.." && pwd)"

# Credentials come from the shell environment. Export the selected provider's
# OPENCODE_REVIEW_REPORT_<P>_URL + OPENCODE_<P>_API_KEY before running (e.g. in your shell
# profile or via direnv); opencode reads them through the {env:...} placeholders
# in opencode.json. The provider is chosen with --provider / OPENCODE_REVIEW_REPORT_PROVIDER
# (default GEMINI). See the "Required environment variables" section of SKILL.md.

# Defaults
PR_NUMBER=""
BASE_BRANCH="main"
OPENCODE_MODEL="gemini-2.5-pro"
# Provider selector (GEMINI | COPILOT | OPENAI | ANTHROPIC | OPENCODE-GO-OPENAI | OPENCODE-GO-ANTHROPIC | OPEN_ROUTER).
# Default GEMINI; override with --provider or the OPENCODE_REVIEW_REPORT_PROVIDER env var. For non-GEMINI providers you must
# also pass a matching --model (and export OPENCODE_REVIEW_REPORT_MODEL_SECONDARY /
# OPENCODE_REVIEW_REPORT_MODEL_ORCHESTRATOR); lib/resolve-provider.sh fails fast otherwise.
OPENCODE_REVIEW_REPORT_PROVIDER="${OPENCODE_REVIEW_REPORT_PROVIDER:-GEMINI}"
POST_REVIEW=false
OPEN_AFTER=false
REVIEW_TYPE="full"

# Parse arguments
while [[ $# -gt 0 ]]; do
  case "$1" in
    --pr)
      PR_NUMBER="$2"
      shift 2
      ;;
    --base)
      BASE_BRANCH="$2"
      shift 2
      ;;
    --model)
      OPENCODE_MODEL="$2"
      shift 2
      ;;
    --provider)
      OPENCODE_REVIEW_REPORT_PROVIDER="$2"
      shift 2
      ;;
    --post)
      POST_REVIEW=true
      shift
      ;;
    --open)
      OPEN_AFTER=true
      shift
      ;;
    --help|-h)
      echo "Usage: local-review.sh [OPTIONS]"
      echo ""
      echo "Run Gemini code review locally using the same scripts as CI."
      echo ""
      echo "Options:"
      echo "  --pr NUMBER          Review a specific PR (fetches metadata via gh CLI)"
      echo "  --base BRANCH        Base branch to diff against (default: main)"
      echo "  --model MODEL        Primary review model ID (default: gemini-2.5-pro). Must"
      echo "                       be a model of the selected provider (e.g. gpt-5.5 for OPENAI)."
      echo "  --provider PROVIDER  GEMINI | COPILOT | OPENAI | ANTHROPIC |"
      echo "                       OPENCODE-GO-OPENAI | OPENCODE-GO-ANTHROPIC |"
      echo "                       OPEN_ROUTER"
      echo "                       (default: GEMINI; or set OPENCODE_REVIEW_REPORT_PROVIDER)"
      echo "  --post               Post review to PR (requires --pr)"
      echo "  --open               Open final review in \$EDITOR after completion"
      echo "  --help, -h           Show this help"
      echo ""
      echo "Prerequisites:"
      echo "  - opencode CLI installed: curl -fsSL https://opencode.ai/install | bash"
      echo "  - The selected provider's gateway creds exported (the gateway host"
      echo "    must be reachable from where you run this — see AGENTS.md):"
      echo "      GEMINI                → OPENCODE_REVIEW_REPORT_GEMINI_URL  + OPENCODE_GEMINI_API_KEY"
      echo "      COPILOT               → OPENCODE_REVIEW_REPORT_COPILOT_URL + OPENCODE_COPILOT_API_KEY"
      echo "      OPENAI                → OPENCODE_REVIEW_REPORT_OPENAI_URL  + OPENCODE_OPENAI_API_KEY"
      echo "      ANTHROPIC             → OPENCODE_ANTHROPIC_API_KEY     (URL is the fixed api.anthropic.com base)"
      echo "      OPENCODE-GO-OPENAI    → OPENCODE_GO_OPENAI_API_KEY     (URL is the fixed Zen base)"
      echo "      OPENCODE-GO-ANTHROPIC → OPENCODE_GO_ANTHROPIC_API_KEY  (URL is the fixed Zen base)"
      echo "      OPEN_ROUTER           → OPENCODE_OPENROUTER_API_KEY    (URL is the fixed OpenRouter base)"
      echo "  - For any non-GEMINI provider also export OPENCODE_REVIEW_REPORT_MODEL_SECONDARY"
      echo "    and OPENCODE_REVIEW_REPORT_MODEL_ORCHESTRATOR (non-gemini model IDs)"
      echo "  - gh CLI installed and authenticated (for --pr and --post)"
      echo "  - jq installed"
      exit 0
      ;;
    *)
      echo "Unknown option: $1"
      echo "Run with --help for usage"
      exit 1
      ;;
  esac
done

# Auto-bootstrap credentials. The skill (/ai-review:local-review) runs this script
# in a NON-INTERACTIVE shell, which does not source ~/.zshrc (zsh only sources
# ~/.zshenv there) — so credentials a developer exported in .zshrc are invisible.
# Rather than require manual setup, harvest the specific credential export lines
# from the usual shell rc files when they aren't already in the environment. Only
# allowlisted `export VAR=...` lines are parsed (never whole rc files), so this is
# safe regardless of zsh-specific syntax elsewhere in those files.
harvest_var() {
  local var="$1" rc raw val
  [ -n "${!var:-}" ] && return 0   # already in the environment — nothing to do
  for rc in "$HOME/.zshenv" "$HOME/.zprofile" "$HOME/.zshrc" \
            "$HOME/.bash_profile" "$HOME/.bashrc" "$HOME/.profile"; do
    [ -f "$rc" ] || continue
    raw=$(grep -E "^[[:space:]]*export[[:space:]]+${var}=" "$rc" 2>/dev/null | tail -1)
    [ -z "$raw" ] && continue
    # Assign by parsing (never eval) — the value can contain URL/query chars
    # (?, &, ;, =) that would break `eval`. Strip one layer of surrounding
    # quotes; a lone leading quote (trailing inline comment) takes up to the
    # next matching quote.
    val="${raw#*=}"
    case "$val" in
      \"*\") val="${val#\"}"; val="${val%\"}" ;;
      \'*\') val="${val#\'}"; val="${val%\'}" ;;
      \"*)   val="${val#\"}"; val="${val%%\"*}" ;;
      \'*)   val="${val#\'}"; val="${val%%\'*}" ;;
    esac
    printf -v "$var" '%s' "$val"
    export "$var"
    return 0
  done
  return 1
}
# Harvest every provider's credential pair; lib/resolve-provider.sh picks the
# pair for the selected OPENCODE_REVIEW_REPORT_PROVIDER and validates it below.
for v in OPENCODE_REVIEW_REPORT_GEMINI_URL OPENCODE_GEMINI_API_KEY \
         OPENCODE_REVIEW_REPORT_COPILOT_URL OPENCODE_COPILOT_API_KEY \
         OPENCODE_REVIEW_REPORT_OPENAI_URL OPENCODE_OPENAI_API_KEY \
         OPENCODE_ANTHROPIC_API_KEY \
         OPENCODE_GO_OPENAI_API_KEY \
         OPENCODE_GO_ANTHROPIC_API_KEY \
         OPENCODE_OPENROUTER_API_KEY; do
  harvest_var "$v" || true
done

# Validate prerequisites
if ! command -v opencode &>/dev/null; then
  echo "❌ opencode CLI not found. Install with: curl -fsSL https://opencode.ai/install | bash"
  exit 1
fi

# Resolve the selected provider → provider-id + gateway creds, and fail fast on
# missing creds / a model chain that doesn't match the provider. Export the model
# chain FIRST so the resolver can validate it (primary = --model arg). These are
# also what review-in-chunks.sh / aggregate-reviews.sh consume.
export OPENCODE_REVIEW_REPORT_PROVIDER
export OPENCODE_REVIEW_REPORT_MODEL_PRIMARY="$OPENCODE_MODEL"
export OPENCODE_REVIEW_REPORT_MODEL_SECONDARY="${OPENCODE_REVIEW_REPORT_MODEL_SECONDARY:-gemini-2.5-pro}"
export OPENCODE_REVIEW_REPORT_MODEL_ORCHESTRATOR="${OPENCODE_REVIEW_REPORT_MODEL_ORCHESTRATOR:-gemini-3-flash-preview}"
# shellcheck source=lib/resolve-provider.sh
source "$SCRIPT_DIR/lib/resolve-provider.sh"

# Install opencode.json so the selected provider resolves, THEN health-check.
# (Config must be in place before `opencode serve` starts.)
bash "$SCRIPT_DIR/lib/setup-opencode-config.sh"

# Health check (provider-agnostic): start `opencode serve`, hit its
# /global/health, tear it down (lib/opencode-health.sh). This replaced the old
# per-provider gateway preflight. NOTE: /global/health confirms opencode itself
# is up; it does NOT verify the upstream gateway is reachable, so it no longer
# pre-empts a private-network/VPN hang — the process-group timeout shim below
# still bounds any hang during the actual model calls.
if ! bash "$SCRIPT_DIR/lib/opencode-health.sh"; then
  echo "❌ opencode health check failed (see output above). Aborting."
  exit 1
fi

if ! command -v jq &>/dev/null; then
  echo "❌ jq not found. Install with: brew install jq"
  exit 1
fi

if [ "$POST_REVIEW" = true ] && [ -z "$PR_NUMBER" ]; then
  echo "❌ --post requires --pr NUMBER"
  exit 1
fi

if [ -n "$PR_NUMBER" ] && ! command -v gh &>/dev/null; then
  echo "❌ gh CLI not found. Install with: brew install gh"
  exit 1
fi

cd "$REPO_ROOT"

echo "=========================================="
echo "🔍 Gemini Local Code Review"
echo "=========================================="
echo "Model: $OPENCODE_MODEL"
echo "Base branch: $BASE_BRANCH"
[ -n "$PR_NUMBER" ] && echo "PR: #$PR_NUMBER"
echo ""

# Set up working directory
# CI scripts hardcode ci_temp/ paths. We use .context/ai-review/ (gitignored)
# and symlink ci_temp -> .context/ai-review/ so existing scripts work unchanged.
# IMPORTANT: The ci_temp symlink is created AFTER any temp commit (to avoid git picking it up).
REVIEW_TIMESTAMP="$(date +%Y%m%d-%H%M)"
WORK_DIR=".context/ai-review/${REVIEW_TIMESTAMP}"
# rm -rf (not -f): a prior run may have left ci_temp as a real directory/junction
# (the symlink fallback on Windows without Developer Mode) — `rm -f` can't remove
# a dir and would crash under `set -e`, and a leftover dir makes `ln -sf` nest the
# symlink inside it.
rm -rf ci_temp
mkdir -p "$WORK_DIR/reviews"

# macOS compatibility shims
# CI scripts use GNU tools (sed -i, timeout) that differ or don't exist on macOS
mkdir -p "$WORK_DIR/bin"

# GNU sed shim (macOS BSD sed requires -i '' while GNU uses -i)
if ! sed --version &>/dev/null 2>&1; then
  if command -v gsed &>/dev/null; then
    ln -sf "$(command -v gsed)" "$WORK_DIR/bin/sed"
    echo "ℹ️  Using gsed for macOS compatibility"
  else
    # Create a wrapper that translates GNU sed -i to BSD sed -i ''
    cat > "$WORK_DIR/bin/sed" << 'SHIM_EOF'
#!/bin/bash
# BSD sed compatibility shim: translates GNU `sed -i 'pattern'` to `sed -i '' 'pattern'`.
# Cuddled backup suffixes (`-i.bak`) are already accepted by BSD sed and pass through unchanged.
args=()
i_flag=false
for arg in "$@"; do
  if [ "$i_flag" = true ]; then
    # Previous arg was -i, this is the pattern — insert '' before it
    args+=("" "$arg")
    i_flag=false
  elif [ "$arg" = "-i" ]; then
    args+=("-i")
    i_flag=true
  else
    args+=("$arg")
  fi
done
# If -i was the last arg (no pattern follows), just pass through
exec /usr/bin/sed "${args[@]}"
SHIM_EOF
    chmod +x "$WORK_DIR/bin/sed"
    echo "ℹ️  Using BSD sed compatibility wrapper"
  fi
fi

# GNU timeout shim (macOS has no timeout; use gtimeout or a perl fallback)
if ! command -v timeout &>/dev/null; then
  if command -v gtimeout &>/dev/null; then
    ln -sf "$(command -v gtimeout)" "$WORK_DIR/bin/timeout"
    echo "ℹ️  Using gtimeout for macOS compatibility"
  else
    # Create a portable timeout shim using perl (available on macOS).
    # IMPORTANT: run the command in its OWN process group and kill the whole
    # group on timeout. A naive `alarm; exec bash …` does NOT work — bash traps
    # SIGALRM for its own use and won't die, and even if it did, the opencode
    # grandchild would be orphaned and keep running (root cause of the local
    # review "deadlock": an unreachable gateway made opencode hang and the old
    # shim never killed it).
    cat > "$WORK_DIR/bin/timeout" << 'SHIM_EOF'
#!/bin/bash
# Portable timeout shim for macOS — kills the whole process group on timeout.
DURATION="$1"
shift
DURATION="${DURATION%s}"   # "300s" -> "300"
exec perl -e '
  my $dur = shift;
  my $pid = fork();
  if (!defined $pid) { die "fork: $!"; }
  if ($pid == 0) { setpgrp(0,0); exec @ARGV or exit 127; }
  $SIG{ALRM} = sub { kill("KILL", -$pid); exit 124; };
  alarm $dur;
  waitpid($pid, 0);
  my $st = $?;
  alarm 0;
  exit($st >> 8 ? ($st >> 8) : ($st & 127 ? 128 + ($st & 127) : 0));
' -- "$DURATION" "$@"
SHIM_EOF
    chmod +x "$WORK_DIR/bin/timeout"
    echo "ℹ️  Using perl-based timeout shim for macOS"
  fi
fi

export PATH="${REPO_ROOT}/${WORK_DIR}/bin:$PATH"

# Stub GITHUB_OUTPUT so existing scripts don't fail
export GITHUB_OUTPUT="${WORK_DIR}/github_output.txt"
touch "$GITHUB_OUTPUT"

# Set mandatory context files (same as workflow env).
# These paths are resolved against the repo being reviewed, not this skill's repo.
# Paths that don't exist in the target repo warn-and-skip (cross-repo contract).
export MANDATORY_CONTEXT_FILES=".docs/nfr/PROJECT_SETUP_AGENTS.md .agents/skills/code-review-standards/SKILL.md .docs/nfr/TOOL_SETUP_AGENTS.md .agents/rules-scoped/backend/testing-standards.instructions.md .agents/rules-scoped/backend/dotnet-standards.instructions.md"

# Model chain (OPENCODE_REVIEW_REPORT_MODEL_PRIMARY/SECONDARY/ORCHESTRATOR) + provider were
# already exported and validated above, before sourcing lib/resolve-provider.sh,
# so review-in-chunks.sh / aggregate-reviews.sh inherit them here.

# --- Step 1: Determine SHAs and get changed files ---

PR_TITLE="Local Review"
PR_AUTHOR="$(git config user.name 2>/dev/null || echo 'local')"
HEAD_REF="$(git rev-parse --abbrev-ref HEAD)"

if [ -n "$PR_NUMBER" ]; then
  echo "📋 Fetching PR #${PR_NUMBER} metadata..."
  PR_JSON=$(gh api "repos/{owner}/{repo}/pulls/${PR_NUMBER}" 2>/dev/null) || {
    echo "❌ Failed to fetch PR #${PR_NUMBER}. Check gh auth and PR number."
    exit 1
  }

  PR_TITLE=$(echo "$PR_JSON" | jq -r .title)
  PR_AUTHOR=$(echo "$PR_JSON" | jq -r .user.login)
  HEAD_REF=$(echo "$PR_JSON" | jq -r .head.ref)
  BASE_BRANCH=$(echo "$PR_JSON" | jq -r .base.ref)
  PR_BODY=$(echo "$PR_JSON" | jq -r '.body // ""')

  echo "  Title: $PR_TITLE"
  echo "  Author: @$PR_AUTHOR"
  echo "  Branches: $HEAD_REF → $BASE_BRANCH"

  echo "$PR_BODY" > "$WORK_DIR/pr_description.txt"
  echo ""
fi

# Calculate merge base
echo "📊 Calculating diff..."
TO_SHA="$(git rev-parse HEAD)"
MERGE_BASE="$(git merge-base "origin/${BASE_BRANCH}" "$TO_SHA" 2>/dev/null)" || {
  echo "⚠️  Could not find merge base with origin/${BASE_BRANCH}. Trying ${BASE_BRANCH}..."
  MERGE_BASE="$(git merge-base "${BASE_BRANCH}" "$TO_SHA" 2>/dev/null)" || {
    echo "❌ Could not find merge base. Make sure ${BASE_BRANCH} exists."
    exit 1
  }
}
FROM_SHA="$MERGE_BASE"

echo "  Merge base: ${MERGE_BASE:0:7}"
echo "  HEAD: ${TO_SHA:0:7}"

# Check for uncommitted changes and create a temporary commit if needed
# (review-in-chunks.sh requires commit SHAs for git diff FROM..TO)
TEMP_COMMIT=false

# Check if there are uncommitted changes (staged + unstaged + untracked, excluding ci_temp)
if [ -n "$(git status --porcelain --ignore-submodules | grep -v -e '^?? ci_temp' -e '^?? \.context/')" ]; then
  # Check if there are committed changes on this branch already
  COMMITTED_CHANGES=$(git diff --name-only "${MERGE_BASE}..${TO_SHA}" | wc -l | tr -d ' ')

  if [ "$COMMITTED_CHANGES" -eq 0 ]; then
    echo "  ℹ️  No committed changes — creating temporary commit for review..."
    # Stage all changes EXCEPT ci_temp and create a temp commit
    git add -A -- . ':!ci_temp' ':!.context'
    git commit -m "temp: local-review snapshot (will be reset)" --no-verify --quiet
    TEMP_COMMIT=true
    TO_SHA="$(git rev-parse HEAD)"
    echo "  Temp commit: ${TO_SHA:0:7}"
  else
    echo "  ⚠️  Uncommitted changes exist but won't be reviewed (only committed diffs are used). Consider committing or stashing first."
  fi
fi

# Create ci_temp symlink NOW (after any temp commit, before CI scripts need it)
# On Windows (Git Bash without Developer Mode), ln -s fails silently.
# Fallback chain: symlink → Windows junction (mklink /J) → real directory.
rm -rf ci_temp 2>/dev/null || true
if ln -sf "$WORK_DIR" ci_temp 2>/dev/null && [ -d "ci_temp" ]; then
  : # symlink OK (Linux/macOS, or Windows with Developer Mode)
elif command -v cmd.exe &>/dev/null; then
  # Windows: directory junction works without Developer Mode
  WORK_DIR_WIN="$(cygpath -w "$WORK_DIR" 2>/dev/null | tr '/' '\\')"
  cmd.exe //c "mklink /J ci_temp \"${WORK_DIR_WIN}\"" >/dev/null 2>&1 || true
fi
if [ ! -d "ci_temp" ]; then
  echo "ℹ️  Using ci_temp as real directory (symlink/junction unavailable)"
  mkdir -p ci_temp
  WORK_DIR="ci_temp"
  export GITHUB_OUTPUT="${WORK_DIR}/github_output.txt"
  touch "$GITHUB_OUTPUT"
fi

# Generate changed files list (null-delimited, matching CI format)
git diff --name-only -z "${MERGE_BASE}..${TO_SHA}" > ci_temp/changed_files.txt

if [ ! -s ci_temp/changed_files.txt ]; then
  echo ""
  echo "✅ No changes found between ${BASE_BRANCH} and HEAD. Nothing to review."
  rm -rf "$WORK_DIR" ci_temp
  exit 0
fi

FILES_CHANGED=$(tr '\0' '\n' < ci_temp/changed_files.txt | grep -c . || echo 0)
echo "  Files changed: $FILES_CHANGED"
echo ""

# Set up cleanup trap for temp commit
cleanup_temp_commit() {
  # Remove ci_temp (symlink or junction). Skip if ci_temp IS the WORK_DIR (real-dir fallback).
  if [ "$WORK_DIR" != "ci_temp" ]; then
    rm -f "${REPO_ROOT}/ci_temp" 2>/dev/null
    # rm -f silently fails on Windows junctions (treated as dirs); use cmd.exe rmdir instead
    if [ -e "${REPO_ROOT}/ci_temp" ] && command -v cmd.exe &>/dev/null; then
      cmd.exe //c "rmdir \"$(cygpath -w "${REPO_ROOT}/ci_temp" 2>/dev/null || echo "${REPO_ROOT}/ci_temp")\"" 2>/dev/null || true
    fi
  fi
  if [ "$TEMP_COMMIT" = true ]; then
    echo ""
    echo "🧹 Cleaning up temporary commit..."
    cd "$REPO_ROOT"
    git reset --soft HEAD~1 --quiet 2>/dev/null || true
    git reset HEAD --quiet 2>/dev/null || true
    echo "  ✅ Working tree restored to original state"
  fi
}
trap cleanup_temp_commit EXIT

# --- Step 2: Filter excluded files ---
echo "🔧 Filtering excluded files..."
bash "$SCRIPT_DIR/filter-excluded-files.sh" || true
echo ""

# Recount after filtering
if [ -s ci_temp/changed_files.txt ]; then
  FILES_CHANGED=$(tr '\0' '\n' < ci_temp/changed_files.txt | grep -c . || echo 0)
else
  echo "✅ All files were excluded. Nothing to review."
  rm -rf "$WORK_DIR" ci_temp
  exit 0
fi

# --- Step 3: Find context files ---
echo "📚 Finding context files..."
bash "$SCRIPT_DIR/find-context-files.sh"
echo ""

# --- Step 4: Build expertise statement ---
echo "🧠 Detecting file types..."

declare -A EXPERTISE_MAP
EXPERTISE_MAP[CS]="an expert .NET and C# code reviewer"
EXPERTISE_MAP[TS]="an expert Angular and TypeScript code reviewer"
EXPERTISE_MAP[JS]="an expert JavaScript and React code reviewer"
EXPERTISE_MAP[GO]="an expert Go code reviewer"
EXPERTISE_MAP[YML]="an expert DevOps and CI/CD reviewer"
EXPERTISE_MAP[PY]="an expert Python code reviewer"
EXPERTISE_MAP[SQL]="an expert database and SQL reviewer"
EXPERTISE_MAP[JAVA]="an expert Java code reviewer"
EXPERTISE_MAP[DOCKER]="an expert Docker and containerization reviewer"

declare -A DETECTED_EXPERTISE

while IFS= read -r -d '' file; do
  case "$file" in
    *.cs|*.csproj|*.cshtml) DETECTED_EXPERTISE[CS]=1 ;;
    *.ts|*.tsx) DETECTED_EXPERTISE[TS]=1 ;;
    *.js|*.jsx) DETECTED_EXPERTISE[JS]=1 ;;
    *.go|go.mod|go.sum) DETECTED_EXPERTISE[GO]=1 ;;
    *.yml|*.yaml) DETECTED_EXPERTISE[YML]=1 ;;
    *.py|*.pyw|requirements.txt|setup.py|pyproject.toml) DETECTED_EXPERTISE[PY]=1 ;;
    *.java|pom.xml|build.gradle) DETECTED_EXPERTISE[JAVA]=1 ;;
    *.sql) DETECTED_EXPERTISE[SQL]=1 ;;
    Dockerfile*|docker-compose.yml|docker-compose.*.yml) DETECTED_EXPERTISE[DOCKER]=1 ;;
  esac
done < ci_temp/changed_files.txt

EXPERTISE_LIST=()
for key in "${!DETECTED_EXPERTISE[@]}"; do
  EXPERTISE_LIST+=("${EXPERTISE_MAP[$key]}")
done

if [ ${#EXPERTISE_LIST[@]} -gt 0 ]; then
  JOINED_LIST=$(printf ", %s" "${EXPERTISE_LIST[@]}")
  JOINED_LIST=${JOINED_LIST:2}
  if [[ "$JOINED_LIST" == *", "* ]]; then
    JOINED_LIST=$(echo "$JOINED_LIST" | sed 's/\(.*\), /\1 and /')
  fi
  EXPERTISE_AREAS="$JOINED_LIST"
else
  EXPERTISE_AREAS="an expert code reviewer"
fi

PR_LABEL=""
if [ -n "$PR_NUMBER" ]; then
  PR_LABEL=" reviewing GitHub PR #${PR_NUMBER} (${PR_TITLE}) by @${PR_AUTHOR} targeting ${BASE_BRANCH} from ${HEAD_REF}"
else
  PR_LABEL=" reviewing branch ${HEAD_REF} against ${BASE_BRANCH}"
fi

EXPERTISE_STATEMENT="You are ${EXPERTISE_AREAS}${PR_LABEL}."
echo "  Expertise: $EXPERTISE_STATEMENT"
echo ""

# --- Step 5: Run chunked review ---
echo "🚀 Starting chunked review..."
echo ""
bash "$SCRIPT_DIR/review-in-chunks.sh" \
  "$FROM_SHA" \
  "$TO_SHA" \
  "$OPENCODE_MODEL" \
  "$EXPERTISE_STATEMENT"

# Read total_chunks from GITHUB_OUTPUT
TOTAL_CHUNKS=$(grep '^total_chunks=' "$GITHUB_OUTPUT" | tail -1 | cut -d= -f2)
TOTAL_CHUNKS="${TOTAL_CHUNKS:-1}"

echo ""
echo "🔗 Aggregating reviews..."
echo ""
bash "$SCRIPT_DIR/aggregate-reviews.sh" \
  "$TOTAL_CHUNKS" \
  "$OPENCODE_MODEL" \
  "$REVIEW_TYPE" \
  "$FROM_SHA" \
  "$FILES_CHANGED" \
  "$TO_SHA" \
  "$EXPERTISE_STATEMENT" \
  "none" || {
  # aggregate-reviews.sh may fail mid-flight (model error, network glitch,
  # parser issue). If pr_summary.md exists but final_review.md doesn't,
  # assemble a minimal final review from what we have.
  echo "⚠️  Aggregation script failed — attempting recovery..."
  if [ -f ci_temp/pr_summary.md ] && [ ! -f ci_temp/final_review.md ]; then
    cp ci_temp/pr_summary.md ci_temp/final_review.md
    echo "  ✅ Recovered final review from pr_summary.md"
  elif [ -f ci_temp/combined_reviews.md ] && [ ! -f ci_temp/final_review.md ]; then
    cp ci_temp/combined_reviews.md ci_temp/final_review.md
    echo "  ✅ Recovered final review from combined_reviews.md"
  fi
}

# --- Step 6: Output results ---
echo ""
echo "=========================================="
echo "✅ Review Complete"
echo "=========================================="

if [ -f ci_temp/final_review.md ]; then
  REVIEW_SIZE=$(wc -c < ci_temp/final_review.md | tr -d ' ')
  echo "📄 Review saved to: ${WORK_DIR}/final_review.md (${REVIEW_SIZE} bytes)"

  # Extract recommendation
  RECOMMENDATION=$(grep "MACHINE_READABLE_ACTION" ci_temp/final_review.md | head -1 || echo "")
  if [ -n "$RECOMMENDATION" ]; then
    echo "📋 $RECOMMENDATION"
  fi

  # Post to PR if requested
  if [ "$POST_REVIEW" = true ] && [ -n "$PR_NUMBER" ]; then
    echo ""
    echo "📤 Posting review to PR #${PR_NUMBER}..."

    REVIEW_ACTION=$(grep '^review_action=' "$GITHUB_OUTPUT" | tail -1 | cut -d= -f2)

    case "$REVIEW_ACTION" in
      approve)
        gh pr review "$PR_NUMBER" --approve --body-file ci_temp/final_review.md
        echo "✅ Posted as APPROVE"
        ;;
      request_changes)
        gh pr review "$PR_NUMBER" --request-changes --body-file ci_temp/final_review.md
        echo "✅ Posted as REQUEST_CHANGES"
        ;;
      *)
        gh pr review "$PR_NUMBER" --comment --body-file ci_temp/final_review.md
        echo "✅ Posted as COMMENT"
        ;;
    esac
  fi

  # Open in editor if requested
  if [ "$OPEN_AFTER" = true ]; then
    ${EDITOR:-less} ci_temp/final_review.md
  fi

  echo ""
  echo "📄 Review saved to: ${WORK_DIR}/final_review.md (${REVIEW_SIZE} bytes)"
else
  echo "⚠️  No final review file generated"
  exit 1
fi
