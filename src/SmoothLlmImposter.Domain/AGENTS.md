# Domain — Layer Context

## TL;DR

Pure domain model — entities, aggregate roots, and value objects. Zero external dependencies and no I/O.

## Non-Negotiables

- **No outward dependencies.** Domain references no other project and no infrastructure packages (EF Core, ASP.NET, HTTP, serialization). It is the innermost Clean Architecture layer — everything depends on it, it depends on nothing.
- **No I/O or framework concerns.** No persistence, network, logging, or DI registration here — those belong in Infrastructure/Host.
- **Enforce invariants at construction.** Guard required state in constructors/factory methods so an entity cannot exist in an invalid state. Value objects are immutable and compared by value.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-24 | Renamed to nested `AGENTS.md` (Claude Code auto-discovery). No behavior change. | #152 |
| 2026-05-30 | Created — empty Clean Architecture domain skeleton (`Entities/`, `ValueObjects/`). | — |
