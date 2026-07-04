# ai-analyse

## TL;DR

Autonomous counterpart to `/ai-review`: consumes the OpenCode Review Report's low/medium findings and applies only safe, scoped edits in CI. Critical and high findings remain human-owned.

## Non-Negotiables

- Touch only 🟡 Medium and 🔵 Low findings from the trusted gate-authored review body.
- Never edit for 🔴 Critical or 🟠 High findings, even if their suggested fixes appear nearby.
- Headless CI runs edit-only: no `git`, no commit, no push. The workflow owns those side effects.
- Never emit or create a `/ai-review` trigger. The workflow commit carries an `[ai-analyse]` marker for traceability only — the loop is bounded by the incremental-cycle cap (`OPENCODE_ANALYSE_MAX_INCREMENTAL`), not by a head-commit sentinel. A no-edit cycle (all SKIP) ends the loop early because nothing is pushed.

## Key Behaviors

- The workflow inlines `SKILL.md` into the prompt because opencode headless `run` does not auto-activate project skills and the `analyse` agent has the `skill` tool disabled.
- The agent is intentionally edit-only in `.agents/skills/ai-review-report/assets/opencode.json`: read/list/grep/glob/edit allowed; bash, skill, task, webfetch, and websearch denied.
- Summary comments are posted by the existing `.agents/skills/ai-review/scripts/copilot-review.sh summary` helper so GitHub plumbing stays centralized.

## Changelog

| Date | Change | Ref |
|------|--------|-----|
| 2026-06-30 | Initial AGENTS.md for the `ai-analyse` skill: autonomous low/medium fixer, edit-only in CI, `[ai-analyse]` traceability marker, incremental-cycle cap. | ai-analyse |
