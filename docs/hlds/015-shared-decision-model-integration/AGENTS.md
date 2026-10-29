# AGENTS.md — Shared Decision-Model Integration

## TL;DR

Read [README.md](README.md) first — the shared-integration design, LADRs and NFRs live there.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-22 | Goals approved; LADR-01..04 and NFR-01..03 drafted | #138 |
| 2026-10-24 | Count-free settings condition; LADR-02 records three shipped transport deviations; LADR-03 gate-as-component | #156 |
| 2026-10-24 | Extraction implemented and Accepted: `DecisionServiceOptions` + `IDecisionServiceConsumer`/`DecisionServiceDemand` in Application, `DecisionServiceGate` in Infrastructure; startup check is a hosted service, not `ValidateOnStart` (a startup validator would report an invalid routing config twice) | #156 |
| 2026-10-25 | Draft LADR-05 per-section profiles, circuits, gates; LADR-03 retained as single-consumer case | #164 |
| 2026-10-25 | HLD 017 Accepted: LADR-05 Accepted | #164 |
| 2026-10-28 | LADR-05 item 1 settings half: each section resolves its profile fields section → shared `DECISIONS_*` → default; the client still uses one profile until WT-23 | #225 |
| 2026-10-28 | LADR-05 items 1 and 3 implemented: section-named calls, named profile per section, gate per endpoint+model; LADR-03 and NFR-01 read per gate key | #228 |
| 2026-10-29 | LADR-05 item 3 amended: `LocalLlmSlot` shared by every decision section and the optimizer replaces the keyed gates; retry delay layered setting; NFR-01 budget restated | #247 |
