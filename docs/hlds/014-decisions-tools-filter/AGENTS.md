# AGENTS.md — Decision-Model Tool Pre-Filter

## TL;DR

Read [README.md](README.md) first — decision-tool filtering intent, decisions and quality requirements live there.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-28 | Sticky selection requires `ToolOrdering.Enabled`; startup fails otherwise (LADR-13, LADR-09 amendment) | #232 |
| 2026-10-26 | `ToolSearch`/`Agent` always kept regardless of `AlwaysKeep` (LADR-08). Claude Code ≥2.1.306 tool-search catalogs carry a `DeferredToolPlaceholder`, so those passes skip anyway; the guard covers a catalog without it | — |
| 2026-10-25 | Circuit breaker designed, not implemented: per-consumer, one failed call opens it, optional `DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS` cooldown (default 180), Error on open (LADR-11, NFR-05) | #164 |
| 2026-10-25 | LADR-09 shared memory gains per-instance lifetime and uncapped mode for HLD 017 cache | #164 |
| 2026-10-25 | Draft LADR-12 edit-return contract; overlap forwards original | #164 |
| 2026-10-25 | HLD 017 Accepted: LADR-12 Accepted | #164 |
| 2026-10-24 | Skips logged at Information with a reason; Codex Responses requests (tools only in `additional_tools`) intentionally unfiltered (LADR-10) | — |
| 2026-10-23 | Sticky per session: identity from inbound body, never the transformed one (LADR-09) | — |
| 2026-10-23 | Memo reuse distinct from upstream cache hits; optional ordering is independent | — |
| 2026-10-23 | Core-tool `AlwaysKeep`, `MinKeepTools` floor, threshold 0.50 (LADR-08) | #138 |
| 2026-10-23 | Conductor/local-command wrappers stripped from state; fail-open log names status and model | — |
| 2026-10-23 | Memo keyed per tool question; only valid answers cached; text between reminder spans kept (review) | #141 |
| 2026-10-25 | Circuit breaker implemented (LADR-11): consumer-local, one failed call opens, trial admission, `circuit-open` skip | — |
| 2026-10-23 | Request-first state (no post-request assistant text); need-question wording; all-skip keeps catalog; deferred-loading skip; chunk memo; byte-preserving output; threshold 0.20 | #138 |
| 2026-10-23 | Classifier success logs use canonical request shape instead of caller-controlled paths | #140 |
| 2026-10-23 | Conductor paste-in installer forwards decision settings; existing UI scripts require replacement/re-run | #138 |
| 2026-10-22 | HLD 014 accepted; HTTP(S)/hosted-choice caveats; HLD 015 proposed, not superseding; AMD64/cache gates pending | #138 |
