# LADR-02 — Dual stamp (OpenAI) + cache-key preference

<!-- Status lifecycle: Draft → Prototype → Accepted. -->

- **Status:** Accepted · 2026-07-24
- **Context:** opencode research shows both `session_id` body and `x-opencode-session` header; Anthropic body injection is riskier. Live diag probe could not confirm which signal is sufficient (model unsupported).
- **Decision:** Stamp **both** on OpenAI; **header only** on Anthropic. Carry `session_id` through `ToChatCompletions`. When Responses caching is enabled and a session was resolved, set `prompt_cache_key` to the session identity instead of the inbound model name.
- **Dual-stamp posture (OpenAI):** the resolved identity is written to **both** the `session_id` body field and the `x-opencode-session` header. The live diag probe could not confirm which channel opencode-go's cache-routing layer consults (model unsupported in the workspace), so the dual stamp is committed as a deliberate belt-and-braces hedge, **not** contingent on a pending result. **Exit criterion:** once the opencode-go maintainers confirm which single channel is authoritative, a follow-up LADR removes the redundant channel; until then both are permanent.
- **Consequences:** Body stamp must not be gated on the Responses-only caching branch. Follow-up live diag validation remains open (informational — it narrows to one channel, it does not gate shipping the dual stamp).

## Alternatives Considered

- **Header-only** (`x-opencode-session` on both dialects). Rejected: live
  probe of opencode-go showed header was accepted but model-id still
  rejected — body channel may be the one opencode-go consults for cache
  routing.
- **Body-only** (`session_id` on OpenAI). Rejected: Anthropic has no
  body field equivalent; header is the only Anthropic surface.

## Related

- [LADR-01: Fourth rewrite class, opt-in](./LADR-01-fourth-rewrite-class-opt-in.md)
- [LADR-03: Resolve precedence](./LADR-03-stateless-resolve-precedence.md)
- `OpenAiRequestTransformer.ToChatCompletions` `session_id` allowlist
