# AGENTS.md — Routing Optimization (HLD 017)

## TL;DR

Strategy interface + coordinator for HLD 017 message optimization. Read [LADR-08](../../../../../docs/hlds/017-message-optimizer/ladrs/LADR-08-parallel-strategies.md).

## Non-Negotiables

- **NativeAOT: no assembly scanning.** Strategies registered explicitly in `DependencyInjection.AddApplication`.
- **Overlap rule:** two edits claiming the same JSON pointer → forward the original body.
- **Byte-preserving merge:** top-level patches use the raw `DecisionJson.ReplaceTopLevel` writer; nested patches use `JsonPointerPatcher` text spans. Never re-serialise the whole body (NFR-01).
- **Failure isolation:** a throwing/cancelled/timed-out branch contributes no edit; others still apply. Caller cancellation propagates.
- **One harness tag source.** `HarnessSpans` is shared by HLD 014 extraction and `HumanSpanExtractor`; never duplicate the tag arrays in a consumer.
- **Captured sessions only.** `MessageChecksumCache` refuses derived or missing session identities and never includes the inbound model in its scope.
- **Validation is deterministic and absolute.** Any changed or added protected element rejects the rewrite; only one targeted retry is allowed, then the original is forwarded (LADR-04).
- **Catalog names are conditional.** A protected tool/MCP name is required in the rewrite only when the original mentions it; never reject a rewrite for an unrelated catalog entry.

## Key Behaviors

- **Checksum cache:** SHA-256 over the original human text plus the optimizer version; the first stored value is final; a hit replays bytes and makes no model call. A whole scope drops only after its sliding TTL (default/max 120 h) and there is no scope or entry cap.
- **Per-section circuits:** the gate and optimizer each own a `SectionCircuitBreaker`; one service failure opens that section only, an open section makes zero calls, and a cooldown admits one trial. Caller cancellation, local gate waits and invalid answers never trip. Open gate continues to the optimizer; open optimizer forwards the original.
- **Rewrite validation:** `Validate` compares protected tokens (code, links, paths, numbers, versions, env vars, negations, commands, mentions, identifiers), rejects introduced tokens, harness tags and supplied instruction lines, and compares heading/list/fence structure. `ValidateWithRetryAsync` forwards the original after one failed retry; failed-element values are for the retry prompt, never for logs (NFR-04).
- **Strategy flow:** candidates are user messages in array order, handled sequentially and gated once per message. A message whose human segments are all cached replays without a model call; a gate decline records every uncached human segment as the original; a gate failure or an open gate continues to the optimizer; optimizer failure or an open optimizer records the original; a discarded rewrite records the original. Within a message, each human segment shorter than `MinChars` skips both models and is not cached; a switch message, assistant/system turn, tool-result turn and non-text block are skipped. Only text pointers are patched; harness spans and non-text blocks stay byte-identical.
- **Inbound scope:** the strategy skips Responses inbound requests even when the Host has downgraded them to a Chat body (Open item 4). `OptimizationRequest.InboundPath` carries the pre-transform path; the effective `Path` alone cannot distinguish a downgraded Responses request from a real Chat request.
- **Transport resolution:** `LlmMessagesOptions` is resolved from the post-configured `ImposterOptions.LlmMessages` by `LlmMessagesOptionsSetup`. That setup is load-bearing: without it the client sees an empty `BaseUrl`/`Model`/`ApiKey` and the cache and optimizer circuit ignore the operator's TTL and cooldown.
- **Log hygiene:** the strategy and coordinator log exception types (with section/model/endpoint), never the exception object, because a `JsonException` can carry message or reply text (NFR-04).
- **Gate transport:** `MessageGate` posts one choice question through the shared decision client. It currently inherits the shared tool-section transport/model because per-section `DECISIONS_MESSAGES_*` profiles are not implemented; do not assume `DECISIONS_MESSAGES_*` transport settings apply yet.

## Test References

- **L0** `Application.UnitTest/Routing/Optimization` — span extraction (excluded spans, byte reassembly, unclosed openers), checksum cache (first value, version, scope, TTL, sessionless), rewrite validation (accepting/rejecting fixture per protected class, introduced elements, structure, length, echo, one-retry policy), message gate probability, strategy flow (patches, replay, minimum, one gate per multi-segment message, decline/positive/failure/open/bypass, downgraded Responses skip, retry, switch, tool-result, cancellation, type-only failure logs), `LlmMessagesOptionsSetupTests` (structured and conventional `LLM_MESSAGES_*` precedence), and per-section circuits (trip class, cooldown, trial close/reopen, endpoint/log shape, integration with both strategy calls).
- **L0** `Infrastructure.UnitTest/Clients/Optimizer` — request shape, bearer auth, retry status policy, caller cancellation, and the full-response attempt timeout (a stalled body must not outlive `TimeoutSeconds`).
- **L2** `Host.IntegrationTest/MessageOptimizerIntegrationTests` — the real optimizer client through the coordinator seam rewrites a Chat message with the configured `LLM_MESSAGES_*` destination, and a downgraded `/responses` request makes no gate or optimizer call.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-25 | Scaffolded optimization abstractions + coordinator (WT-02) | #164 |
| 2026-10-25 | Human spans use the shared HLD 014 tag source; checksum cache + reserved settings | #164 |
| 2026-10-25 | Rewrite validation: full protected class set, structure, echo, one-retry policy | #164 |
| 2026-10-25 | Message strategy + gate + `MIN_CHARS`; shared gate transport caveat | #164 |
| 2026-10-26 | Per-section gate/optimizer circuits, trial admission, Error/Information log shape | #164 |
| 2026-10-26 | Host forward-seam coordinator wiring; byte-preserving top-level/nested merge | #164 |
| 2026-10-26 | Review fixes: bind `LlmMessagesOptions`; skip downgraded Responses; full-response optimizer timeout; one gate per message; type-only failure logs | #164 |
