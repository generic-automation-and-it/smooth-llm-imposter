# {{TITLE}} — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Owner** | <team / handle> |
| **Tracker** | [<project / epic name>](<url>) |
| **Last updated** | {{DATE}} |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker.

## Intent

<2–4 sentences. What is being introduced and why now. State the problem and the
target outcome. No marketing language.>

## Key Goals

### 1. <Goal>

<1–4 paragraphs: the change, the pattern/principle behind it, the side effects.
Concrete examples beat abstractions.>

**Acceptance criteria / DoD**

- <Observable, testable condition that means this goal is met.>
- <Another condition. These are the design's definition-of-done, not test cases.>

### 2. <Goal>

<...>

**Acceptance criteria / DoD**

- <...>

## Core Separation of Concerns

> <One blockquoted thesis sentence — the load-bearing premise the design rests on.>

<1–2 paragraphs expanding the thesis.>

## Guiding Principle — <Tagline>

> <One blockquoted slogan.>

- <Ownership / independence rule.>
- <What we will deliberately NOT do.>

---

## Diagrams

- [System Context (C1) + supporting diagrams](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–N are strategic (*what* and *why*); later LADRs are tactical (*how*). Each is a
single decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-example.md) | <Title — one-line summary> | Draft |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-example.md) | <Attribute> | <measurable target> | Draft |
