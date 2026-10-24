# AGENTS.md — Anthropic Messages to Chat Bridge

## TL;DR

Read [README.md](README.md) first — intent, goals, decisions and quality requirements live there. The `spec/` files are the normative behavior; start with [00](spec/00-cross-cutting-decisions.md), then 01 to 04.

## Working rules

- Each spec file's edge-case matrix is the conformance test list; write tests from the rows, not from prose.
- Do not edit the "same-dialect only" rule in the routing `*AGENTS.md` until this HLD is Accepted; [LADR-01](ladrs/LADR-01-per-provider-opt-in.md) records the narrowing.
- Provider option plumbing must follow the checklist in LADR-01 and the options table in spec 01; an option missing from the cloner or the admin DTOs ships inert.
- Specification only: there is no implementation plan here; sequencing belongs in the tracker.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-24 | HLD authored in Discovery: LADR-01..09, NFR-01..04, spec 00..04 | — |
