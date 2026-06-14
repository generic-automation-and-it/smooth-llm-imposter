# git-commit-push — AGENTS.md

## TL;DR

Middle of the git skill chain: delegates committing to `git-commit` (haiku-tier sub-agent), then pushes via `scripts/push.sh`, which owns upstream tracking, the nothing-to-push case, and `--issue`-driven branch rename.

## Key Behaviors

- `scripts/push.sh --rename <branch>` renames and pushes with `--set-upstream` in one step; the *derivation* of the conforming branch name (`<type>/<issue>-slug`) stays with the agent — the script never invents names.
- "Nothing to commit or push" is a graceful no-op by contract, not an error — `git-commit-push-pr` relies on this to update a PR description without new commits.
- `--mansplain` and `--issue` are pass-through arguments: `--mansplain` forwards down to `git-commit`; `--issue` is consumed here (rename) but its number is re-derived upstream by `git-commit-push-pr` from the branch name.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. Push/rename plumbing moved from SKILL.md prose into `scripts/push.sh` (relocated from git-commit-push-pr, extended with `--rename`). | |
