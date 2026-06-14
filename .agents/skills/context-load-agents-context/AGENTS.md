# context-load-agents-context — AGENTS.md

## TL;DR

Hook + skill wrapper around `scripts/load-agents-context.sh`; full design rationale (six LADRs, sequence diagram, transfer checklist) lives in `references/design.md` — read it before modifying the script.

## Non-Negotiables

- **The script must always exit 0.** It runs as a `PostToolUse` hook; a non-zero exit blocks the user's Read/Edit/Write. Swallow all file-operation failures.
- **Never emit files from auto-loaded rule dirs** (`.agents/rules/`, `.claude/rules/`, `.cursor/rules/`, `.github/instructions/`) — they're already in context; re-emitting doubles their token cost.

## Key Behaviors

- Session dedup canonicalises physical paths, so the same AGENTS.md reached via `.claude/...` and `.agents/...` symlinks is emitted once — breaking canonicalisation silently doubles context injection.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. | |
