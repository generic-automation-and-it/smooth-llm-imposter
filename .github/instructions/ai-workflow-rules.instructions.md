---
description: 'AI-assisted coding workflow phases (Odin→Bragi) — mandatory execution order for all coding tasks'
globs: "**"
paths:
  - "**"
applyTo: '**'
alwaysApply: true
---
# AI-Assisted Coding Workflow

Standard execution workflow for all coding tasks. Aligned with `AI_WORKTASK_PROMOTE_STANDALONE_TEMPLATE.md`. Updated: 2026-02-25

## Phase 0: Context Load (MANDATORY BLOCKING)

**A functional `*_AGENTS.md` file MUST be loaded into the conversation before ANY execution or planning.**

Root AGENTS.md / NFR files alone are NOT sufficient. Domain-specific context is required.

| Working in | Apply |
|------------|-------|
| Backend code (.NET, C#, server-side) | `.agents/rules/backend/*` rules (scoped to `**/*.cs` via frontmatter; attach when a C# file is opened) |

All rules under `.agents/rules/` are auto-loaded every session, organized into category subfolders (`backend/`, `git/`, `meta/`); applicability is scoped per-file via frontmatter (`paths`/`globs`/`applyTo`). For functional `*_AGENTS.md` context: use the `load-context` skill with `[domain]` or manually request the relevant files. The Rule Categories table in root `AGENTS.md` lists each rule and what it covers.

If no context loaded: **BLOCK** → offer: Load / Search / Create / BYPASS.

## Phase 1: Odin (Clarify) — GATE

Ask clarifying questions about requirements, constraints, business logic (not technical implementation details — decide those autonomously).

- **Never assume** — when unsure about anything, ask
- If requirements are already clear from the task description, state your understanding and proceed without waiting
- Otherwise, **wait for user response before proceeding**

## Phase 2: Thoth (Analyze)

Analyze tech stack and existing patterns relevant to the task.

Skip for: single-line fixes, obvious bugs, config changes.

## Phase 3: Forseti (Specify)

Create technical specification with architectural decisions.

Skip for: simple tasks where implementation is obvious.

## Phase 4: Tyr (Plan) — GATE

**Prerequisites:** Phase 1 complete. If Phases 2/3 were not skipped, their output informs this plan.

Present implementation plan. **Wait for user approval before proceeding.**

Use for: 3+ files, architectural decisions, complex logic, cross-cutting concerns, new features.
Skip for: single-line fixes, obvious bugs, config changes.

## Phase 5: Frigg (Document)

Update project documentation with approved plan (e.g., AGENTS.md under `## Requirements`).

Skip when: no documentation convention exists or changes are trivial.

## Phase 6: Thor (Execute) — YOLO MODE

Implement autonomously. No permission asks. Complete implementations (no TODOs). Fix forward.

**On failure:** Attempt to fix forward. If blocked after 2 attempts, report status with suggested options and wait for user guidance.

## Phase 7: Heimdall (Review)

Quality gate — spec/implementation sync check.

**Checklist:** Code quality | Tests | Pattern consistency | Security | Spec/implementation sync | Changelog

**Sync check:** Compare specification (Phase 3) or task instructions (if Phase 3 was skipped) vs implementation. If mismatch → warn user, offer: update spec / update logic / manual review.

**→ After review, proceed to Phase 8 (Bragi). Do not stop here.**

## Phase 8: Bragi (Record) — 🛑 MANDATORY

**Do not skip this phase.** Document what was **actually implemented** (not just planned):

- Update the context document loaded in Phase 0 with real implementation details
- Finalize LADRs with actual outcomes
- Review whether any `.docs/adrs/` or `.docs/nfrs/` need updating — if the implementation changes behavior covered by an existing ADR or NFR, update it; if a new architectural decision was made, create a new ADR
- Update changelog with comprehensive entry

**If a context document was loaded in Phase 0, it MUST be updated in Phase 8.** Loading a context document creates a mandatory update obligation — this is not optional regardless of path (lightweight or full).

**If no context document existed:** Create from `TEMPLATE_AGENTS.md`, add to root AGENTS.md.

**Why this is mandatory:** The Gemini code review gate requires `*_AGENTS.md` changes on FULL reviews. Skipping Phase 8 will block PRs. This phase also ensures AI-generated changes are documented for future conversations.

## Output Requirements

**MANDATORY for every conversation — no exceptions:**

1. **Label every phase** — Output `## Phase N: Name` as a visible header before executing each phase
2. **Label every skip** — If skipping a phase, output: `## Phase N: Name — Skipped: [one-line reason]`
3. **Sequential execution** — Phases MUST execute in declared order. Never reorder, combine, or nest phases (e.g., doing Phase 5 work inside Phase 6 is a violation)
4. **No silent phases** — Every phase in your chosen path (lightweight or full) MUST appear in output. If the user can't see it, it didn't happen

**Why this matters:** Without visible phase labels, skipped phases are invisible to the user. This makes it impossible to verify workflow compliance or catch when phases are silently dropped.

## Enforcement

| Phase | Name | Gate? | Level |
|-------|------|-------|-------|
| 0 | Context Load | 🛑 MANDATORY | No context = no execution |
| 1 | Odin (Clarify) | 🛑 GATE | State understanding if clear, otherwise wait for response |
| 2 | Thoth (Analyze) | | Skippable — MUST output skip reason |
| 3 | Forseti (Specify) | | Skippable — MUST output skip reason |
| 4 | Tyr (Plan) | 🛑 GATE | Wait for user approval before proceeding |
| 5 | Frigg (Document) | | Skippable — MUST output skip reason |
| 6 | Thor (Execute) | | 🔨 YOLO MODE — implement autonomously |
| 7 | Heimdall (Review) | | Mandatory sync check — MUST proceed to Phase 8 |
| 8 | Bragi (Record) | 🛑 MANDATORY | Update context doc, review ADRs/NFRs, update changelog |

### Path Selection

**Lightweight path**: Phase 0 → 1 → 6 → 7 → 8
Use when: single file, no architectural decisions, clear requirements.

**Full path**: Phase 0 → 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8
Use when: 3+ files, new patterns, cross-cutting concerns, or ambiguous scope.

**Phase 8 is on BOTH paths.** If a context document was loaded in Phase 0, Phase 8 is mandatory regardless of which path was chosen. The only exception is if no context document was loaded AND the change is truly trivial (e.g., single-line typo fix).

**Path selection is NOT a way to avoid Phase 8.** Changing 30 config files is not "lightweight" even if each change is simple. The path determines which middle phases (2–5) to run, not whether to document.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
