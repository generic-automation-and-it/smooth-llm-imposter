# HLD 001 — LLM Imposter Routing

Status: Accepted · 2026-06-14

## Problem

Teams want to transparently redirect specific model calls to alternate, cheaper, or local upstreams
without changing client code or storing credentials — and have the router add prompt caching that the
target upstream doesn't apply itself. This is a sibling of the Smooth Claude Proxy, with three key
differences: it stores no keys, it accepts **both** OpenAI and Anthropic dialects, and routing is a
configurable array of per-provider model mappings rather than one fixed route.

## Solution overview

A stateless ASP.NET Core minimal-API service. Inbound dialect is determined by the endpoint
(`/v1/chat/completions`, `/v1/responses` → OpenAI; `/v1/messages` → Anthropic). For each request the
router reads `model`, selects the first matching provider mapping (config order), rewrites the model,
optionally injects caching, applies the provider's configured key, and streams the upstream response back
unbuffered. The service **imposters only on configured model types**: an unmatched model returns a
dialect-shaped 404 (the shipped config declares no default provider — clients point their SDK directly at
the real provider for calls they don't want impostered; see [LADR-005](ladrs/LADR-005-no-default-passthrough-type-only.md)).
A dialect-default passthrough provider remains optionally supported in code. Routing is **same-dialect
only** — there is no OpenAI⇄Anthropic body translation.

## Diagrams

- [System context](diagrams/system-context.md) — actors and external upstreams.
- [Request flow](diagrams/request-flow.md) — plan → forward → stream sequence.
- [Routing decision](diagrams/routing-decision.md) — endpoint → dialect → match/passthrough/404.

## Configuration

- Bound from the `Imposter` section; **environment variables override `appsettings.json`** (env wins),
  e.g. `Imposter__Providers__4__Secret=sk-...`. The sibling `AuthScheme` (`ApiKey`|`Bearer`, case-insensitive)
  selects the auth header and defaults by dialect when omitted (openai → Bearer, anthropic → ApiKey).
- A **provider** = `Name` + `Dialect` + `BaseUrl` (server root, no `/v1`; the inbound request path is
  appended verbatim) + `Secret` + optional `AuthScheme` + optional `IsDefault`, plus `OpenAiUpstreamApi` (`responses` default or
  `chat_completions` for OpenAI-compatible upstreams without `/responses`), holding nested `Models[]` of `{ From, To, Caching }`.
  `From` supports exact + trailing-`*` wildcard. A provider with no `Models` is inert until one is added.
- Keys are configuration-only and never persisted. Startup validation (`ValidateOnStart`) rejects unknown
  dialects, non-absolute base URLs, duplicate names, malformed mappings, and >1 default per dialect.

```jsonc
"Imposter": { "Providers": [
  { "Name": "opencode-go", "Dialect": "openai", "BaseUrl": "https://opencode.ai/zen/go", "Secret": "", "AuthScheme": "ApiKey",
    "OpenAiUpstreamApi": "chat_completions",
    "Models": [ { "From": "gpt-5.4", "To": "kimi-k2.7", "Caching": true } ] },
  { "Name": "openrouter", "Dialect": "openai", "BaseUrl": "https://openrouter.ai/api", "Secret": "", "AuthScheme": "Bearer",
    "OpenAiUpstreamApi": "chat_completions" },
  { "Name": "opencode-anthropic", "Dialect": "anthropic", "BaseUrl": "https://opencode.ai/zen/go", "Secret": "", "AuthScheme": "ApiKey",
    "Models": [ { "From": "claude-haiku-*", "To": "minimax-m3", "Caching": true } ] }
] }
```

## Architecture

Clean Architecture, no persistence: `Domain` (routing value objects + matcher) → `Application`
(`Features/Routing`: options, catalog, resolver, transformers, router, error factory) → `Infrastructure`
(`UpstreamForwarder` over `IHttpClientFactory`) → `Host` (endpoints + composition). Body transformation is
pure string-in/string-out in Application; all HTTP I/O is in Host; Infrastructure is `System.Net.Http` only.

## Non-functional requirements

- [NFR-001 — Statelessness](nfrs/NFR-001-statelessness.md)
- [NFR-002 — Credential security](nfrs/NFR-002-credential-security.md)
- [NFR-003 — Streaming pass-through](nfrs/NFR-003-streaming-passthrough.md)
- [NFR-004 — Configuration precedence & validation](nfrs/NFR-004-configuration-precedence-validation.md)

## Architecture decisions (LADRs)

- [LADR-001 — No Mediator / no FluentValidation request pipeline](ladrs/LADR-001-no-mediator-no-fluentvalidation.md)
- [LADR-002 — Stateless, no EF Core / PostgreSQL](ladrs/LADR-002-stateless-no-ef-postgresql.md)
- [LADR-003 — Infinite client timeout, no resilience handler](ladrs/LADR-003-infinite-timeout-no-resilience-handler.md)
- [LADR-004 — Integration tests stub the outbound transport in-process](ladrs/LADR-004-in-process-transport-stub.md)
- [LADR-005 — Type-only impostering, no default passthrough configured](ladrs/LADR-005-no-default-passthrough-type-only.md)

## Out of scope (for now)

Cross-dialect translation, `count_tokens` interception, per-model response handlers, usage tracking, and
`/v1/models` passthrough. The transformer/forwarder seams leave room to add these.
