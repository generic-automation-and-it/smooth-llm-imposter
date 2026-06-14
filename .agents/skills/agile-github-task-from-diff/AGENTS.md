# agile-github-task-from-diff — AGENTS.md

## TL;DR

Script-driven skill: `create_github_task_from_diff.py` owns diff classification, issue authoring, project add, sub-issue linking, and the branch-rename suggestion; the agent only decides the feature link and executes the suggested rename.

## Non-Negotiables

- **Don't author the issue title/body by hand** — the script is the source of truth for the `[layer]` title format and acceptance-criteria checklist. Hand-authored issues drift from the horizontal-slicing contract.
- **Never guess a branch `<type>` or slug** when the script's suggestion looks wrong — stop and ask, per the format-enforcement rule in `git-policy.instructions.md`.

## Key Behaviors

- `classify_horizontal_slice()` keys the `backend` layer off path prefixes `src/` and `Project` — when this template repo is renamed, that prefix list must be updated or backend changes silently classify as `general`.
- The script's `LAYER_TO_TYPE` map drives the suggested branch name printed after creation; `git-commit-push-pr` later parses the issue number back out of that branch name for its `Closes #` link — the two skills are coupled through the `<type>/<issue>-slug` convention, not through any shared code.
- Sub-issue linking uses a REST endpoint (`POST .../sub_issues`) that fails soft: the script prints a manual-link fallback instead of erroring, so a "created but not linked" outcome is normal, not a bug.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. Branch-rename derivation moved from SKILL.md prose into the script (`suggest_branch_name`); stale `ProsmarBunkering` path prefix removed. | |
