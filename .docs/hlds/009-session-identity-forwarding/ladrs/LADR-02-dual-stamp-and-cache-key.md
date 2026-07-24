# LADR-02 — Dual stamp (OpenAI) + cache-key preference

- **Status:** Accepted · 2026-07-24
- **Context:** opencode research shows both `session_id` body and `x-opencode-session` header; Anthropic body injection is riskier. Live diag probe could not confirm which signal is sufficient (model unsupported).
- **Decision:** Stamp **both** on OpenAI; **header only** on Anthropic. Carry `session_id` through `ToChatCompletions`. When Responses caching is enabled and a session was resolved, set `prompt_cache_key` to the session identity instead of the inbound model name.
- **Consequences:** Body stamp must not be gated on the Responses-only caching branch. Follow-up live diag validation remains open.
