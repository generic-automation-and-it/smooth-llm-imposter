# Session Identity Forwarding — Agent Guardrails

## TL;DR

Opt-in `SessionForwarding=opencode-go` stamps a per-request session identity on matched imposter
routes so opencode-go diag can group Codex/Claude traffic. Capture → derive → none; never store;
never log raw values.

## Non-Negotiables

- **Matched imposter + opt-in only.** Passthrough/default routes must not stamp body or managed header.
- **No session store.** Resolve from the current request only (NFR-001 / HLD 009 NFR-01).
- **Never invent a random id.** If nothing stable exists, leave `session=none`.
- **Never log raw session values or fingerprint inputs.** Information logs use `session=captured|derived|none` only.
- **OpenAI dual stamp; Anthropic header-only.** Do not inject fields into Anthropic Messages bodies.
- **Responses→Chat must carry `session_id`.** The `ToChatCompletions` allowlist is explicit — add new body fields there deliberately.
- **Body stamp is not gated on Responses caching.** opencode-go uses `chat_completions`; `session_id` must still be written.
- **Header write is drop-then-write once** (`x-opencode-session`), mirroring managed auth.

## Key Behaviors

- Config: `ProviderOptions.SessionForwarding` + `<PREFIX>_SESSION_FORWARDING` + admin CRUD field.
- Resolver order: headers `session_id` → `x-opencode-session` → `x-session-id` → `conversation_id`; body `prompt_cache_key` → `metadata.user_id`; else fingerprint of stable caller material.
- Caching interaction: when Responses caching is on and a session was resolved, `prompt_cache_key` uses the session identity; otherwise inbound model name (unchanged).
- Live diag probe against opencode-go was blocked on model availability in the implementation workspace — treat dual-stamp as research-backed with follow-up validation noted in the HLD README.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-07-24 | Initial HLD 009 — session identity forwarding for opencode-go. | #72 |
