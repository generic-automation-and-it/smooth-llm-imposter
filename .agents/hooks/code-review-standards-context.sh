#!/usr/bin/env bash
# Hook: Inject code-review-standards rule on review-related prompts.
# Event: UserPromptSubmit
#
# Why: code-review-standards only matters during reviews, so it is NOT
# auto-loaded every Claude session (its `paths` frontmatter is a non-matching
# sentinel). This hook re-injects it on demand when the prompt is review-related,
# saving ~900 tokens on every non-review session.
#
# Cursor/Copilot still load the rule unconditionally via their own frontmatter
# (`globs`/`alwaysApply`/`applyTo`), since they do not run Claude Code hooks.
#
# Triggers on:
#   - /review, /security-review, /code-review slash commands
#   - "review the PR", "PR review", "gemini review", "code review"

set -euo pipefail

PAYLOAD=$(cat 2>/dev/null || true)
PROMPT=$(printf '%s' "$PAYLOAD" | jq -r '.prompt // empty' 2>/dev/null || true)
[ -z "$PROMPT" ] && exit 0

if ! printf '%s' "$PROMPT" | grep -qiE '/(security-)?review($|[^a-z])|/code-review($|[^a-z])|gemini review|pr review|code review|review (the |this )?pr'; then
    exit 0
fi

REPO_ROOT=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
RULE_FILE="${REPO_ROOT}/.agents/rules/code-review-standards.instructions.md"
[ -f "$RULE_FILE" ] || exit 0

printf '<context-auto-loaded>\n\n## Context: .agents/rules/code-review-standards.instructions.md\n'
cat "$RULE_FILE" 2>/dev/null || true
printf '\n</context-auto-loaded>\n'
exit 0
