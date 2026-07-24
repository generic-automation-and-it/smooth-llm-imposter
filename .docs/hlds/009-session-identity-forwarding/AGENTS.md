# Session Identity Forwarding — Agent Guardrails

<!-- AI Context: HLD 009 — session identity forwarding. Updated: 2026-07-24. -->

> AI-coder context for this HLD. Architecture diagrams live in
> [./diagrams/](./diagrams/), the full decision log in
> [./ladrs/](./ladrs/) and [./nfrs/](./nfrs/), and the human-readable
> narrative in [README.md](./README.md).

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
- **Header write is drop-then-write once** — drop caller-relayed `session_id` and `x-opencode-session`, then stamp `x-opencode-session` once, mirroring managed auth.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [./ladrs/](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-01 | Session forwarding is a fourth sanctioned request-rewrite class — opt-in, matched-imposter only | Stamping on a passthrough/opt-out route leaks caller identity upstream and breaks the "disabled ⇒ byte-identical body+header bytes" invariant (NFR-02) |
| LADR-02 | Dual-stamp `x-opencode-session` header + `session_id` body on OpenAI; header-only on Anthropic; `session_id` rides the `ToChatCompletions` allowlist. When Responses caching is enabled **and** a session was resolved, `prompt_cache_key` is set to the session identity (else inbound model name) | Single-channel was rejected — the live opencode-go probe authed but rejected the model id, so the sufficient signal is unconfirmed; body-only is impossible on Anthropic. Gating the body stamp on Responses caching would drop it for `chat_completions`; gating `prompt_cache_key` on caching-disabled or session-unresolved would either be a no-op or break the session cache-key promise |
| LADR-03 | Stateless resolve precedence: headers → body → SHA-256 fingerprint of stable caller material → none; never a random id | An in-memory per-caller bucket would violate NFR-01; wrong precedence buckets distinct callers together and leaks identity into the upstream cache key |

## Key Behaviors

- Config: `ProviderOptions.SessionForwarding` + `<PREFIX>_SESSION_FORWARDING` + admin CRUD field.
- Resolver order: headers `session_id` → `x-opencode-session` → `x-session-id` → `conversation_id`; body `prompt_cache_key` → `metadata.user_id`; else fingerprint of stable caller material.
- Caching interaction: when Responses caching is on and a session was resolved, `prompt_cache_key` uses the session identity; otherwise inbound model name (unchanged).
- Live diag probe against opencode-go was blocked on model availability in the implementation workspace — treat dual-stamp as research-backed with follow-up validation noted in the HLD README.

## Quality Constraints

Measurable NFRs live in [./nfrs/](./nfrs/). Constraints that change how code is written:

- **No persistence** — resolution is a pure function of the current request; no new storage types/tables (NFR-01).
- **Disabled ⇒ byte-identical** — with `SessionForwarding` unset, forwarded body and header bytes equal the inbound bytes on both imposter and passthrough routes (NFR-02).
- **Log token only** — `SessionIdentity.LogToken` emits `captured|derived|none`; raw session values and fingerprint inputs never reach a log sink, and all four capture headers are masked in both inbound and outbound Debug dumps via `SensitiveHeaderNames` (NFR-03).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-07-24 | Initial HLD 009 — session identity forwarding for opencode-go (3 LADRs, 3 NFRs). | #72 |
