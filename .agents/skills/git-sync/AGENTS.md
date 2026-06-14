# git-sync — AGENTS.md

## TL;DR

`scripts/safe-sync.sh` does the deterministic fetch+merge and reports via sentinels; the agent only interprets them — resolution policy stays in SKILL.md, plumbing stays in the script.

## Non-Negotiables

- **`MERGE_ERROR` is not a conflict.** Dirty tree, missing ref, or failing hook (exit 2) must never be "resolved" — report and stop. Only `MERGE_CONFLICTS` (exit 1) enters resolution, and only in `--fix` mode.
- **Conflicts are intentionally left unmerged in the working tree** so either the user (default mode) or the agent (`--fix`) resolves from the same state. Don't add `--abort` cleanup to the script.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. | |
