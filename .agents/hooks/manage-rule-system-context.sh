#!/usr/bin/env bash
# UserPromptSubmit hook — injects the manage-rule-system SKILL.md when the
# user mentions creating/updating/modifying a rule or instruction file.
# stdout is appended verbatim to the user prompt.

set -euo pipefail

if [ -t 0 ]; then
    PROMPT=""
else
    PAYLOAD=$(cat)
    PROMPT=$(printf '%s' "$PAYLOAD" | jq -r '.prompt // empty' 2>/dev/null || true)
fi

# Match common phrasings for rule-system management.
# Positive: "create a rule for X", "update the testing rule", "edit .agents/rules/foo".
# Negative: "review the PR", "fix the bug" — must NOT fire.
if printf '%s' "$PROMPT" | grep -qiE '(create|update|modif|add|edit|new|restructur).{0,40}(rule|instruction)|\.agents/rules|\.ai/rules|\.claude/rules'; then
    SCRIPT="$(cd -P "$(dirname "$0")" && pwd -P)/../skills/manage-rule-system/scripts/inject-context.sh"
    [ -x "$SCRIPT" ] && exec "$SCRIPT" || true
fi

exit 0
