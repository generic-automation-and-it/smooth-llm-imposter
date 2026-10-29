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
| 2026-10-29 | LADR-05 rule 3 amended: one process-wide local-LLM gate (`LocalLlmGate`) shared by every decision section and the optimizer instead of one gate per endpoint+model; wait bounded by `LOCAL_LLM_GATE_WAIT_SECONDS`; one retry after 5 s by default; NFR-01 budget restated | NO-TICKET |
