#!/usr/bin/env bash
# Skill: manage-rule-system
# Emits SKILL.md content inside a <context-auto-loaded> envelope, deduped per session.
# Invoked by the UserPromptSubmit hook when the prompt mentions rule creation/editing.

set -euo pipefail

SKILL_DIR=$(cd -P "$(dirname "$0")/.." && pwd -P)
SKILL_MD="${SKILL_DIR}/SKILL.md"
[ -f "$SKILL_MD" ] || exit 0

REPO_ROOT=$(git -C "$SKILL_DIR" rev-parse --show-toplevel 2>/dev/null || true)
ROOT="${REPO_ROOT:-$SKILL_DIR}"

SESSION_ID="${CLAUDE_SESSION_ID:-${CODEX_SESSION_ID:-${CODEX_THREAD_ID:-${COPILOT_SESSION_ID:-${GITHUB_COPILOT_SESSION_ID:-${PPID:-$$}}}}}}"
SAFE_SESSION_ID=$(printf '%s' "$SESSION_ID" | tr -c 'A-Za-z0-9._-' '_')
TRACKER="/tmp/.skill_ctx_manage-rule-system_${SAFE_SESSION_ID}"
touch "$TRACKER" 2>/dev/null || exit 0

if grep -qxF "$SKILL_MD" "$TRACKER" 2>/dev/null; then
    exit 0
fi
printf '%s\n' "$SKILL_MD" >> "$TRACKER" || true

rel="${SKILL_MD#"$ROOT"/}"
printf '<context-auto-loaded>\n\n## Context: %s\n' "$rel"
cat "$SKILL_MD" 2>/dev/null || true
printf '\n</context-auto-loaded>\n'

exit 0
