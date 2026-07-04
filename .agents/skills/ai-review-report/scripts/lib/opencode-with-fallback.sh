#!/bin/bash
# Usage: opencode-with-fallback.sh <primary> <fallback1> <fallback2> -- <prompt-file>
#
# Replaces the previous `gemini -m <model> --yolo < prompt-file` pattern.
# Invokes opencode for each model in turn, returning on the first success.
# Callers pass an explicit chain (LADR-002 / LADR-023):
#   - chunk review:  PRIMARY_REVIEW   SECONDARY_REVIEW   ""
#   - orchestrator:  ORCHESTRATOR     <resolved review model>   ""
# An empty model slot is skipped, so a two-tier chain just leaves fb2 empty.
#
# The provider is selected by OPENCODE_REVIEW_REPORT_PROVIDER (GEMINI|COPILOT|OPENAI|ANTHROPIC|
# OPENCODE-GO-OPENAI|OPENCODE-GO-ANTHROPIC|OPEN_ROUTER) and resolved to its opencode
# provider-id by lib/resolve-provider.sh, which exports OPENCODE_REVIEW_REPORT_PROVIDER_ID
# (gemini / github-copilot / openai / go-openai / go-anthropic / openrouter). This script
# reads that id below; it defaults to gemini when unset so a bare
# invocation keeps the historical Gemini behavior. Credentials are read by
# opencode itself via the {env:OPENCODE_<P>_*} placeholders in opencode.json.
#
# Optional env:
#   OPENCODE_AGENT            Agent name to run (default: review).
#   OPENCODE_MIN_OUTPUT_BYTES Minimum stdout bytes for success (default: 200).
#
# Stdout: opencode output. Stderr: passthrough.

set -e

# Disable Claude Code (.claude) support to avoid conflicts with opencode.
# OPENCODE_REVIEW_REPORT_DISABLE_CLAUDE_CODE can override the value (default 1).
export OPENCODE_DISABLE_CLAUDE_CODE="${OPENCODE_REVIEW_REPORT_DISABLE_CLAUDE_CODE:-1}"

primary="$1"; fb1="$2"; fb2="$3"; shift 3
[ "$1" = "--" ] && shift
prompt_file="$1"; shift

if [ -z "$prompt_file" ] || [ ! -f "$prompt_file" ]; then
  echo "opencode-with-fallback.sh: prompt file missing or not readable: ${prompt_file:-<empty>}" >&2
  exit 64
fi

# Default fallback "gemini" must match the GEMINI provider id declared in
# lib/resolve-provider.sh. The resolver normally sets this env var; the
# fallback is a safety net for bare invocation without sourcing the resolver.
PROVIDER="${OPENCODE_REVIEW_REPORT_PROVIDER_ID:-gemini}"
OPENCODE_AGENT="${OPENCODE_AGENT:-review}"
OPENCODE_MIN_OUTPUT_BYTES="${OPENCODE_MIN_OUTPUT_BYTES:-200}"

run_opencode() {
  # --agent review (LADR-029): pin the locked-down `review` agent from
  #   opencode.json instead of the DEFAULT `build` agent. `review` has read/grep
  #   but NO skill/task/edit/write/bash, so the model cannot self-activate this
  #   repo's own ai-review-report skill (which `build` auto-discovered and fired
  #   on the .github workflow chunk, ending the turn with empty stdout). It still
  #   reads context files; opencode.json sets `permission.external_directory:
  #   allow` (LADR-025) — the headless equivalent of gemini-cli --yolo — globally
  #   and on the agent, so reads of in-repo dot-paths (.github/*, .docs/*,
  #   .agents/rules-scoped/*) are not auto-rejected in non-interactive `run` mode.
  # Prompt is fed via stdin (not "$(cat …)" argv expansion) so large chunks
  #   never hit ARG_MAX; matches the original `gemini < file` call shape.
  # --log-level WARN: keeps stdout clean for the legacy parser surface.
  #   On failure, review-in-chunks.sh's empty-output detector dumps the
  #   chunk file + stderr so diagnostics surface where it matters. To
  #   debug a stuck/flaky chunk locally, re-run with --log-level INFO
  #   --print-logs.
  # --format default: human-readable markdown matching the legacy parser surface
  #   (sed/grep on DETAILED_SECTION_MARKER and per-priority emoji lines).
  # Empty-output guard (LADR-029): opencode can exit 0 while emitting empty/tiny
  #   stdout (silent provider failure, or an agent that spent its turn on tool
  #   calls). Capture the output and FAIL (return 1) when it is below the same
  #   200-byte floor review-in-chunks.sh enforces — so try_run falls through to
  #   the next model in the chain instead of returning a hollow "success" that
  #   short-circuits the fallback. Whatever little came back is echoed to stderr
  #   for diagnostics. Reviews are a few KB, so buffering in a var is safe.
  local _out
  _out=$(opencode run \
    --agent "${OPENCODE_AGENT}" \
    --model "${PROVIDER}/$1" \
    --format default \
    --log-level WARN \
    < "$prompt_file") || return 1
  if [ "${#_out}" -lt "${OPENCODE_MIN_OUTPUT_BYTES}" ]; then
    printf '%s' "$_out" >&2
    return 1
  fi
  printf '%s\n' "$_out"
}

try_run() {
  local model="$1"
  [ -z "$model" ] && return 1
  run_opencode "$model"
}

try_run "$primary" \
  || try_run "$fb1" \
  || try_run "$fb2"
