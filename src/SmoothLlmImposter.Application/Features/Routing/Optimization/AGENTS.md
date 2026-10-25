# AGENTS.md — Routing Optimization (HLD 017)

## TL;DR

Strategy interface + coordinator for HLD 017 message optimization. Read [LADR-08](../../../../../docs/hlds/017-message-optimizer/ladrs/LADR-08-parallel-strategies.md).

## Non-Negotiables

- **NativeAOT: no assembly scanning.** Strategies registered explicitly in `DependencyInjection.AddApplication`.
- **Overlap rule:** two edits claiming the same JSON pointer → forward the original body.
- **Failure isolation:** a throwing/cancelled/timed-out branch contributes no edit; others still apply. Caller cancellation propagates.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-25 | Scaffolded optimization abstractions + coordinator (WT-02) | #164 |