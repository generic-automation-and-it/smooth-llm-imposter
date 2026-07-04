# ai-review — Maintenance Context

## TL;DR

Vendored copy of the `/ai-review` consumer skill from `generic-automation-and-it/smooth-ai-report-review`. It *parses* an AI PR review and applies per-issue fix/skip decisions; review generation lives in the sibling `ai-review-report` skill and the OpenCode review workflow. Treat this directory as a mirror of upstream — fixes belong upstream, not here.

## Non-Negotiables

- **Do not hand-edit the skill logic here to fix bugs.** This is a downstream copy. Changes made locally diverge from upstream and are lost on the next sync. File the fix in the upstream repo, then re-copy.
- **Keep the three review skills distinct.** `ai-review` consumes posted reviews for human fix/skip handling, `ai-review-report` generates review reports and owns shared review tooling, and `ai-analyse` is the autonomous low/medium fixer. Do not merge their scripts or responsibilities.
- **Script-path duality:** `SKILL.md` references `.agents/skills/ai-review/scripts/copilot-review.sh` for this copy-install. The `${CLAUDE_PLUGIN_ROOT}/...` variant in `SKILL.md` applies only when the skill runs from the upstream Claude Code plugin (`smooth-ai-review`) — not here. Keep the copy-install path permitted in `.agents/settings.json`.

## System Context

```mermaid
C4Context
  System(consumer, "ai-review (this skill)", "Parses a posted review; applies fix/skip; routes results")
  System_Ext(gha, "pipeline-code-review-report.yml@v1", "Reusable GHA that GENERATES the review on a PR")
  System_Ext(gh, "GitHub PR", "Review threads / PR description / comments")
  Rel(gha, gh, "posts AI review")
  Rel(consumer, gh, "reads review, replies/resolves threads or appends AI Review Notes")
```

The review toolchain is split by direction: the reusable GHA plus `ai-review-report` produce the review report on a PR; this skill consumes it. The user drives the consumer with `/ai-review <pr>` (analyse) then `/ai-review <pr> 1=fix 2=skip` (execute).

## Key Behaviors

- **Mode auto-detect:** any `N=fix`/`N=skip` arg → execute; otherwise analyse. Analyse always STOPS and never auto-executes.
- **Source routing:** auto-detects Copilot vs other via `copilot-review.sh detect <pr>`. Copilot → reply/resolve each linked review thread + post a summary comment. Other → append the fix/skip table to the PR description's AI Review Notes (append, never overwrite).
- **All deterministic GitHub plumbing** (detect/threads/reply/resolve/summary) lives in `scripts/copilot-review.sh`; the skill keeps only the judgment (parsing + fix/skip + reply text).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-20 | Vendored `/ai-review` consumer skill from smooth-ai-report-review; generator kept remote via thin caller workflow. | |
| 2026-07-04 | Updated context for the local `ai-review-report` and `ai-analyse` skill install plus the `pipeline-code-review-report.yml@v1` caller. | |
