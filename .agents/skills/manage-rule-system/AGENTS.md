# manage-rule-system — AGENTS.md

## TL;DR

Convention skill for `.agents/rules/` files, injected on demand by `scripts/inject-context.sh` (UserPromptSubmit hook) rather than loaded every session — the injection is deduped once per session.

## Key Behaviors

- `inject-context.sh` dedupes via a tracker file in `/tmp` keyed by session id (falls back through `CLAUDE_SESSION_ID` → Codex/Copilot ids → `$PPID`). On id-less runners every prompt re-injects the SKILL.md — if that shows up, fix the session-id chain, not the hook matcher.
- Rule files physically live in `.github/instructions/` (real dir for Copilot's hosted agent); `.agents/rules` is a symlink. Any path advice this skill gives must keep that direction — pointing the symlink the other way breaks github.com-side Copilot.
- The same rule tree is now distributable to other repos via `ai-template-sync --rules-only`; structural changes here (folder layout, frontmatter contract) propagate to consumers on their next pinned sync.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. | |
