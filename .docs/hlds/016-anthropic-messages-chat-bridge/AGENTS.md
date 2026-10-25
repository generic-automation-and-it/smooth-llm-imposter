# AGENTS.md — Anthropic Messages to Chat Bridge

## TL;DR

Read [README.md](README.md) first — intent, goals, decisions and quality requirements live there. The `spec/` files are the normative behavior; start with [00](spec/00-cross-cutting-decisions.md), then 01 to 04.

## Working rules

- Do not edit the "same-dialect only" rule in the routing `*AGENTS.md` until this HLD is Accepted; [LADR-01](ladrs/LADR-01-per-provider-opt-in.md) records the narrowing.
- Provider option plumbing must follow the checklist in LADR-01 and the options table in spec 01; an option missing from the cloner or the admin DTOs ships inert.
- Specification only: there is no implementation plan here; sequencing belongs in the tracker.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-24 | HLD authored in Discovery: LADR-01..09, NFR-01..04, spec 00..04 | — |
| 2026-10-24 | Review pass: usage formula unified across 02/03, relocated auth header allowed in the allowlist, enabling option spec'd in 01, README lists spec 00 | — |
| 2026-10-24 | Cross-checked against LiteLLM's Anthropic-to-Chat bridge: mid-turn system messages mapped, deterministic tool-id map, opt-in cache-control / response-format / document-as-image, tool `strict` forwarded | — |
| 2026-10-25 | Ralph loop fixes recorded in README changelog; deferred items unchanged | HLD 016 |
| 2026-10-24 | Implemented (LADRs/NFRs Accepted); spec corrections and endpoint limitations recorded in README changelog | HLD 016 |
