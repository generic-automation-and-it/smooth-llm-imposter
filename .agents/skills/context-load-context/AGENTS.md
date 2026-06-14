# context-load-context — AGENTS.md

## TL;DR

Pure-prompt Phase 0 gate: discovers and loads `*_AGENTS.md` files by domain glob, and BLOCKS (with create/search/manual/bypass options) when none exist — the block is the feature, not an error path.

## Key Behaviors

- This skill is the interactive front-end of the workflow's Phase 0; the *automatic* path is the `load-agents-context` PostToolUse hook (see `context-load-agents-context/`). Changes to discovery conventions (file naming, template location) must be mirrored in both.
- Created files come from `.agents/templates/TEMPLATE_AGENTS.md` and are deliberately left minimal — population happens in Phase 8 (Bragi), so don't "improve" creation to pre-fill sections.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. | |
