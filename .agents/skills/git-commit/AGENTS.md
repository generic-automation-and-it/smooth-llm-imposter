# git-commit — AGENTS.md

## TL;DR

Base of the git skill chain (`git-commit-push-pr` → `git-commit-push` → `git-commit`); owns logical-unit grouping and conforming commit messages — never commits with a non-conforming message.

## Non-Negotiables

- **The SKILL.md type list is a FALLBACK, not a source of truth.** `git-policy.instructions.md` is authoritative; the inline "Fallback types" list exists only for runners that don't auto-load `.agents/rules/` (e.g. Codex) and MUST be kept in sync with the rule on every vocabulary change — a previous unsynced copy drifted (it allowed `style`, which the policy does not).
- **`--mansplain` must stay forwardable**: it arrives from the parent skills and suppresses every "ask the user" branch. Adding a new interactive question without a `--mansplain` bypass breaks the autonomous chain.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-12 | Initial version. Inline type list replaced with git-policy pointer (drift: list wrongly included `style`). | |
| 2026-06-12 | Type list restored as explicit "Fallback types" (synced to git-policy, `style` dropped) for runners that don't auto-load rules; rule stays authoritative. | |
