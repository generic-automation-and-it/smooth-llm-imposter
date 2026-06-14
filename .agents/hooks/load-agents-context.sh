#!/usr/bin/env bash
# Hook entry point for load-agents-context skill.
# Delegates to the skill script so the implementation stays in one place.
SCRIPT_PATH="$(git -C "$(dirname "$0")" rev-parse --show-toplevel 2>/dev/null || echo ".")/.agents/skills/context-load-agents-context/scripts/load-agents-context.sh"
[ -x "$SCRIPT_PATH" ] && exec "$SCRIPT_PATH" "$@" || exit 0
