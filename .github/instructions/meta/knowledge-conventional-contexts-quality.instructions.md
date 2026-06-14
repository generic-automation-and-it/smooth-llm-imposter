---
description: 'AGENTS.md quality standards, required structure, and anti-patterns'
globs: "**/*AGENTS.md"
paths:
  - "**/*AGENTS.md"
applyTo: '**/*AGENTS.md'
alwaysApply: true
---
# AGENTS.md Quality Standards

Rules for writing and updating `*_AGENTS.md` files. Applies whenever you create or modify an AGENTS.md — including Phase 8 (Bragi) updates. Updated: 2026-02-28

## Purpose

AGENTS.md captures contextual knowledge that source code alone cannot communicate. Its purpose is to improve AI coding quality by preventing mistakes, providing the "why" behind decisions, and building understanding of system boundaries and constraints.

If information can be derived by reading the source code, it does NOT belong in an AGENTS.md file. Every line must earn its place.

## Required Structure

Use exactly these sections, in this order. Omit any section (other than Changelog) that would be empty or "N/A".

1. **TL;DR** — One line. What this does and its most important constraint or behavior.
2. **Non-Negotiables** — Safety guardrails, forbidden patterns, things an AI agent must never do. Only items an AI coder would plausibly get wrong.
3. **System Context** — 2-4 sentences plus diagrams where applicable. Three diagram types, each with specific inclusion criteria:

   **a) C4Context diagram** — High-level system context showing external dependencies. No internal components or data flows.
   - **Include for**: Services, workers, and modules with external integrations (APIs, databases, message queues, third-party systems).
   - **Omit for**: Simple internal components, handlers, UI components with no external dependencies.
   - Format: Mermaid `C4Context` with `System()` and `System_Ext()` nodes.

   **b) Sequence diagram** — High-level interaction flow showing the order of operations and side effects.
   - **Include for**: Handlers, services, or workflows with 3+ steps OR any side effects (emails, SignalR, external API calls, queue messages).
   - **Omit for**: Simple CRUD handlers, single-step operations, pure data transformations.
   - Format: Mermaid `sequenceDiagram`. Show participants as roles (e.g., "Handler", "Repository", "SignalR Hub"), not class names. Keep to 5-10 steps max — this is an overview, not a trace.

   **c) ER diagram** — Entity relationships for data access layers.
   - **Include for**: DbContext files, repositories, or services that operate on 3+ related entities with non-obvious relationships.
   - **Omit for**: Single-entity CRUD, entities whose relationships are obvious from naming (e.g., Order → OrderProduct).
   - Format: Mermaid `erDiagram`. Show per-domain entity clusters (5-10 key entities), not the full data model. Focus on relationships an AI coder would need to understand to write correct queries.

   A single AGENTS.md may include multiple diagram types if applicable (e.g., a service with external deps AND complex entity relationships).
4. **Architecture Decisions** — LADR format (LADR-NNN, Date, Status, Context, Decision, Consequences). Only decisions where the rejected alternative would look reasonable to an AI coder. A decision is NOT trivial if: the alternative has real trade-offs, the reasoning isn't obvious from code, or getting it wrong has non-obvious consequences (e.g., production failures, data corruption, silent sync issues).
5. **Key Behaviors** — Non-obvious behaviors, edge cases, cross-cutting concerns NOT apparent from source code.
6. **Test References** — Test tier (L0/L1) and sub-folder path within test projects. Backend only — frontend tests are co-located and don't need this. Omit if no tests exist. Must be updated when tests are added or modified.
7. **Quality Constraints** — Feature-specific non-functional requirements that go beyond the project-wide baseline in `.agents/rules/non-functional-requirements.instructions.md`. Only include constraints that would change how code is written. Omit if none exist.
8. **Migration Plans** — Planned migrations, deprecations, or technical debt that affects how new code should be written. Include what's changing, the target state, and what to avoid building on. Omit if none exist.
9. **Changelog** — Always include the header, even if empty. One-line rows: `| Date | Change | Ref |`. Keep bug/gotcha/pitfall entries. Remove cosmetic-only entries.

## Anti-Patterns (MUST avoid)

- **No empty sections**: If a section (other than Changelog) would be "N/A" or "None", omit it entirely.
- **No `(src: path)` annotations**: The AI can find files itself.
- **No file listings as "components"**: The AI can glob/grep for files.
- **No restating code**: If the source shows it clearly, don't repeat it.
- **No generic boilerplate**: "Required secrets: AWS_ACCESS_KEY_ID" adds no value.
- **No business value / problem-solution-impact blocks**: Human product context, not AI coding context.
- **No validation checklists that restate expected behavior**.
- **No "see other file" cross-references as section content**: A single line in TL;DR or Key Behaviors suffices.
- **No duplicating root AGENTS.md**: Feature-level docs inherit from root.

## Value Test

Before including any line, apply all three criteria:

1. **Bug prevention**: Would an AI coder write worse code or introduce a bug without this?
2. **Decision quality**: Would an AI coder make a worse architectural or design choice without this?
3. **System understanding**: Does this help an AI coder understand the system's boundaries, constraints, or integration points in a way not apparent from source code?

If none of the three apply, remove it. If at least one applies, keep it — but it must still follow the anti-patterns and section structure rules above.

## Drift Minimization

When AI agents make code changes, they MUST update the corresponding `*_AGENTS.md` context sections to reflect the actual implementation. Drift between code and context documentation degrades AI coding quality over time.

**Rules:**
- **Update obligation**: If a code change modifies behavior documented in an AGENTS.md file (Key Behaviors, Architecture Decisions, System Context diagrams, ER relationships), the AGENTS.md MUST be updated in the same commit or PR.
- **Diagram accuracy**: Sequence diagrams, C4Context diagrams, and ER diagrams must reflect current system state. Adding a new external dependency, handler step, or entity relationship requires updating the relevant diagram.
- **Changelog tracking**: Every AGENTS.md update must add a changelog entry. This creates an audit trail and signals to future AI agents that the file is actively maintained.
- **No stale documentation**: Stale context is worse than no context — it causes AI agents to write code against outdated assumptions. If you notice drift during Phase 0 (Context Load), fix it before proceeding.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
