# AGENTS.md - {{TITLE}}

AI Context: HLD for {{TITLE}}. Updated: {{DATE}}

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

<One line: what this HLD covers + where intent, decisions, and quality spec live.>

## Non-Negotiables

- <Thing an AI coder building against this design would plausibly get wrong.>
- LADRs are Draft/Prototype status — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-01 | <decision> | <consequence of getting it wrong> |

## Key Behaviors

- <Non-obvious runtime/operational truth not apparent from a design read.>

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- <e.g. tenant isolation rule, key-ownership boundary — not a duplicated NFR target.>

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| {{DATE}} | HLD scaffolded | <ticket> |
