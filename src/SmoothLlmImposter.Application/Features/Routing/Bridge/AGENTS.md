# Bridge — Anthropic Messages to Chat Completions (HLD 016)

## TL;DR

Opt-in translation of a matched Anthropic imposter route's Messages request to Chat Completions and the reply back. The normative behavior is `docs/hlds/016-anthropic-messages-chat-bridge/spec/` (00–04); this file carries only local traps the spec does not repeat.

## Non-Negotiables

- **The bridge is a router-gated translator, not a router.** It never picks a provider, renames a model, filters tools or retries. `MapRequest` receives an already model-renamed body and returns a Chat body plus a request-derived context (D17/D21).
- **Non-bridged traffic is byte-identical.** `ProviderRoute.AnthropicChat.IsBridged` is true only for a matched imposter route whose `AnthropicUpstreamApi` is `chat_completions`. Never call the bridge from passthrough/default paths, and never let a new provider option default change a `messages` provider.
- **A bridged route replaces the caller header set with an allowlist.** `UpstreamForwarder` must be called with `useHeaderAllowlist: true`; relaying a caller `cookie`, `user-agent`, `anthropic-*`, `x-stainless-*` or `x-api-key` is a security defect, not a tuning issue.
- **Pass a null query string to `UpstreamForwarder` on a bridged route.** The forwarder appends whatever it is given, so forwarding `?beta=true` violates spec 01 §15 / 04 E01.
- **The stream event channel is bounded (capacity 1), and every read cancels its losing delay/wait task.** An unbounded channel or an abandoned 8-second delay per chunk breaks NFR-02's bounded memory.
- **Tool arguments are validated before emission.** Never substitute `{}` for non-empty broken arguments. On a length cut the call is dropped; otherwise the message fails. The hold buffer is bounded (4 MiB per call, 16 MiB per stream).
- **Numbers keep their textual form.** Parse through `AnthropicChatJson` (raw-token materialisation) and never round-trip through `double`; write compact JSON with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`.
- **`JsonArray.Add` on a concrete node type needs a `(JsonNode)` cast.** The generic overload is trim/AOT-unsafe and NativeAOT treats it as an error, not a warning (the same rule the routing transformers follow).
- **Do not edit the spec to match the code.** A mismatch is a finding: fix the code or raise the spec change explicitly.

## Key Behaviors

- **Seam order:** planning/model rename → `MapRequest` → decision filter on the **Chat** body → one forward → reply mapping. `--who?`/`--newsession` still run first. `AnthropicRequestTransformer` skips cache-breakpoint injection on a bridged route; `forwardCacheControl` is the bridge's own opt-in.
- **Two reply shapes share the same rules.** Non-stream (`AnthropicChatResponseMapper`) and stream (`AnthropicChatStreamTranslator`) share ids, name/id maps, stop reason and usage arithmetic; only framing differs. A 2xx JSON reply to a streaming request is replayed as a complete event sequence.
- **The stream translator is single-writer.** A producer channel parses SSE lines; the async iterator is the only writer of frames, pings and timer actions. `TimeProvider` drives PING_IDLE and FINISH_GRACE, so tests use a manual clock with no sleeps.
- **`count_tokens` is local.** It is answered before forwarding on a bridged route and never calls upstream. A body-less request cannot resolve a provider, so the bridge's endpoint policy (405/404) applies only once a model resolves the bridged route.
- **In-band errors on a 2xx reply are still errors.** They use the in-band overflow rules (any numeric status except 429), keep `retry-after`/`retry-after-ms`, and redact the configured key; stream error frames are redacted in the Host. A choice-level `error` with no content is an in-band error, not an empty 200 message.
- **The dropped attribution block contributes nothing.** The estimator skips it, and `SystemMessage` must not copy its `cache_control` under `forwardCacheControl`.
- **Usage is never empty.** `message_start` carries the request estimate; `message_delta` carries real or estimated figures. `input_tokens` excludes cache read and cache write.

## Test References

- **L0** `Application.UnitTest/Routing/Bridge` — request matrix (spec 01 §18), response matrix (§15), stream matrix (§10), estimator vectors (spec 04 §11.2), provider option validation and catalog materialisation.
- **L2** `Host.IntegrationTest/AnthropicChatBridgeIntegrationTests` — end-to-end non-stream and stream, `count_tokens`, 405/404, error + retry headers, header allowlist, non-bridged transparency.
- **L2** `ProviderConfigAdminIntegrationTests.Admin_round_trip_preserves_anthropic_chat_bridge_options` — GET→PUT round-trip.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-25 | Ralph loop fixes: query-string suppression, bounded stream channel + per-read timer cancellation, in-band overflow/retry/redaction, choice-level errors, attribution cache-control, stream credential wording, dead API removed | HLD 016 |
| 2026-10-24 | Bridge implemented: request/reply/stream mappers, estimator, `count_tokens`, Host seam, header allowlist | HLD 016 |
