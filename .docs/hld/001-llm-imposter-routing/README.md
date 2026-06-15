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
unbuffered. Unmatched models pass through to the dialect's default provider unchanged.

Routing is **same-dialect only** — there is no OpenAI⇄Anthropic body translation (a deliberate scope cut
vs. the proxy's Qwen path; can be added later behind the existing `IRequestTransformer` seam).

## Configuration

- Bound from the `Imposter` section; **environment variables override `appsettings.json`** (env wins),
  e.g. `Imposter__Providers__1__ApiKey=sk-...`.
- A **provider** = `Name` + `Api` (dialect) + `BaseUrl` (server root, no `/v1`) + `ApiKey` + `IsDefault`,
  holding nested `Models[]` of `{ From, To, Caching }`. `From` supports exact + trailing-`*` wildcard.
- Keys are configuration-only and never persisted. Startup validation (`ValidateOnStart`) rejects unknown
  dialects, non-absolute base URLs, duplicate names, malformed mappings, and >1 default per dialect.

## Architecture

Clean Architecture, no persistence: `Domain` (routing value objects + matcher) → `Application`
(`Features/Routing`: options, catalog, resolver, transformers, router, error factory) → `Infrastructure`
(`UpstreamForwarder` over `IHttpClientFactory`) → `Host` (endpoints + composition). Body transformation is
pure string-in/string-out in Application; all HTTP I/O is in Host; Infrastructure is `System.Net.Http` only.

## Key decisions

See `src/SmoothLlmImposter.Application/Features/Routing/ROUTING_AGENTS.md` (LADR-001..004): no
Mediator/FluentValidation request pipeline (opaque proxy bodies); stateless/no EF; infinite-timeout client
with no resilience handler (SSE-safe); in-process stub transport for integration tests (no containers).

## Out of scope (for now)

Cross-dialect translation, `count_tokens` interception, per-model response handlers, usage tracking,
and `/v1/models` passthrough. The transformer/forwarder seams leave room to add these.
