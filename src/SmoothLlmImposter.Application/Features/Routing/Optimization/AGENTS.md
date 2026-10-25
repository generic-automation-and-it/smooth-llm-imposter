# AGENTS.md — Routing Optimization (HLD 017)

## TL;DR

Strategy interface + coordinator for HLD 017 message optimization. Read [LADR-08](../../../../../docs/hlds/017-message-optimizer/ladrs/LADR-08-parallel-strategies.md).

## Non-Negotiables

- **NativeAOT: no assembly scanning.** Strategies registered explicitly in `DependencyInjection.AddApplication`.
- **Overlap rule:** two edits claiming the same JSON pointer → forward the original body.
- **Failure isolation:** a throwing/cancelled/timed-out branch contributes no edit; others still apply. Caller cancellation propagates.
- **One harness tag source.** `HarnessSpans` is shared by HLD 014 extraction and `HumanSpanExtractor`; never duplicate the tag arrays in a consumer.
- **Captured sessions only.** `MessageChecksumCache` refuses derived or missing session identities and never includes the inbound model in its scope.
- **Validation is deterministic and absolute.** Any changed or added protected element rejects the rewrite; only one targeted retry is allowed, then the original is forwarded (LADR-04).
- **Catalog names are conditional.** A protected tool/MCP name is required in the rewrite only when the original mentions it; never reject a rewrite for an unrelated catalog entry.

## Key Behaviors

- **Checksum cache:** SHA-256 over the original human text plus the optimizer version; the first stored value is final; a hit replays bytes and makes no model call. A whole scope drops only after its sliding TTL (default/max 120 h) and there is no scope or entry cap.
- **Reserved settings:** `CircuitBreakSeconds` has no consumer until WT-11 and `DecisionsMessages.MinProbability` has no gate until WT-05. Do not wire either as if it already applies.
- **Rewrite validation:** `Validate` compares protected tokens (code, links, paths, numbers, versions, env vars, negations, commands, mentions, identifiers), rejects introduced tokens, harness tags and supplied instruction lines, and compares heading/list/fence structure. `ValidateWithRetryAsync` forwards the original after one failed retry; failed-element values are for the retry prompt, never for logs (NFR-04).
- **Strategy flow:** candidates are human segments of user messages in array order, handled sequentially. Cache hit replays; below `MinChars` skips both models and is not cached; a switch message, assistant/system turn, tool-result turn and non-text block are skipped. A gate decline records the original; gate failure continues to the optimizer; optimizer failure or a discarded rewrite records the original. Only text pointers are patched; harness spans and non-text blocks stay byte-identical.
- **Gate transport:** `MessageGate` posts one choice question through the shared decision client. It currently inherits the shared tool-section transport/model because per-section profiles are not implemented; do not assume `DECISIONS_MESSAGES_*` transport settings apply yet.

## Test References

- **L0** `Application.UnitTest/Routing/Optimization` — span extraction (excluded spans, byte reassembly, unclosed openers), checksum cache (first value, version, scope, TTL, sessionless), rewrite validation (accepting/rejecting fixture per protected class, introduced elements, structure, length, echo, one-retry policy), message gate probability, and strategy flow (patches, replay, minimum, gate, fail-open, retry, switch, tool-result, cancellation).

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-25 | Scaffolded optimization abstractions + coordinator (WT-02) | #164 |
| 2026-10-25 | Human spans use the shared HLD 014 tag source; checksum cache + reserved settings | #164 |
| 2026-10-25 | Rewrite validation: full protected class set, structure, echo, one-retry policy | #164 |
| 2026-10-25 | Message strategy + gate + `MIN_CHARS`; shared gate transport caveat | #164 |