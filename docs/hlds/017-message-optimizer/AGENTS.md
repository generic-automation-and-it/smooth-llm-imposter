# AGENTS.md — Decision-Gated Message Optimizer

## TL;DR

Read [README.md](README.md) first — intent, goals, decisions and quality requirements live there.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-25 | Authored in Discovery: LADR-01..07, NFR-01..04, Open items 1..8 | #164 |
| 2026-10-25 | Amended: LADR-08 parallel strategies; per-message candidates (Q21); isolated optimizer requests; switch exclusion | #164 |
| 2026-10-25 | Review fixes: Core Separation restored; HLD 014 LADR-05/07 dependency corrected; Open item 8 resolved | #164 |
| 2026-10-25 | LADR-05 replaced: session-scoped checksum cache (sliding TTL ≤ 5 days, uncapped, memory only); sessionless not cached | #164 |
| 2026-10-25 | Dependencies reference HLD 015 LADR-05 Draft per-section amendment | #164 |
| 2026-10-25 | Dependencies reference HLD 014 LADR-12 Draft edit-return amendment | #164 |
| 2026-10-25 | Accepted by owner decision: LADR-05 and LADR-12 Accepted; Phase B ungated; own LADR-01..08 and NFR-01..04 remain Draft | #164 |
| 2026-10-27 | Open item 6 resolved: per-provider `OptimizeMessages` opt-out (message strategy only) | #207 |
| 2026-10-27 | NFR-01 L2 replay added (`MessageOptimizerCachePrefixStabilityTests`); Linux native runners still open | #209 |
| 2026-10-27 | NFR-02 fixtures and corpus added; validator now protects hyphenated dollar commands and counts repeated negations | #212 |
| 2026-10-27 | NFR-03 failure cost proven by an L2 file over the real clients and pipelines, plus a host-platform native smoke; no production change | #214 |
| 2026-10-27 | NFR-04 message egress proven by an L2 request/log capture and a native smoke; two wording gaps recorded (Debug inbound dump, span hash not logged); no production change | #216 |
| 2026-10-27 | NFR-04 wording amended to exclude the opt-in Debug request dump; span hash on the Debug outcome line tracked as follow-up WT-20 | #216 |
| 2026-10-28 | LADR-04: optimizer gets the message as delimited data; validator rejects a rewrite keeping < 60% of content words; instruction revision 2 (WT-21) | #223 |
| 2026-10-28 | LADR-06 settings implemented: shared `DECISIONS_*` layer, `DECISIONS_MESSAGES_*` transport settings, effective-state validation; the gate uses them from WT-23 | #225 |
| 2026-10-28 | Dependencies: message gate calls its own decision profile; gate per endpoint+model (HLD 015 LADR-05) | #228 |
