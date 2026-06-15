# Routing — Feature Context

## TL;DR

Stateless same-dialect LLM router: reads the inbound `model`, rewrites it to a configured upstream
("imposter") or passes through to the dialect's default provider, optionally injecting prompt caching,
and streams the response back. Design rationale lives in `.docs/hld/001-llm-imposter-routing/`
(`README.md` index → `diagrams/`, `nfrs/`, `ladrs/` subfolders).

## Non-Negotiables

- **Never persist, log, or echo `ApiKey` values.** They live only in `ImposterOptions` (config/env). Logs
  carry provider name + model names only. The inbound caller's own `Authorization`/`x-api-key` is **not**
  forwarded — the provider's configured key replaces it.
- **Same-dialect only.** Do not add OpenAI⇄Anthropic body translation here. An `openai` provider serves
  openai requests; an `anthropic` provider serves anthropic requests.
- **`BaseUrl` is the server root WITHOUT a version path** (`https://api.openai.com`, not `.../v1`). The
  inbound request path is appended verbatim; adding `/v1` to config double-prefixes the path.
- **Do not add a standard resilience handler to the `imposter-upstream` client.** SSE streams outlive its
  timeouts and a half-streamed POST can't be replayed; the client uses an infinite timeout bounded by the
  caller's `CancellationToken` (see HLD LADR-003).
- **All body work stays string-in/string-out in Application; HTTP I/O stays in Host.** Infrastructure is
  `System.Net.Http` only — don't leak `HttpContext` into Application/Infrastructure.
- **No Mediator / FluentValidation request pipeline here** (opaque proxy bodies). Validation is on
  configuration at startup (`ImposterOptionsValidator`), not on requests (HLD LADR-001).

## Key Behaviors

- **First match wins, in configuration order.** The resolver scans the dialect's providers top-to-bottom and
  returns the first `Models[].From` that matches; order providers/mappings from most to least specific.
- **`From` matching** is exact or single trailing-`*` wildcard (`claude-haiku-*`), case-insensitive (`ModelMatcher`).
- **No match → default passthrough** (model unchanged, no caching) via the dialect's `IsDefault` provider.
  No match **and** no default → `RoutingException(404)`. At most one `IsDefault` per dialect (startup-validated).
- **Caching is per-dialect** (only when `Caching: true`): Anthropic injects ephemeral `cache_control` on the
  `system` block (a string `system` is converted to a one-element block array) and on the last content block
  of the last message; OpenAI sets `prompt_cache_key` to the **inbound** model name.
- **Errors are dialect-shaped**: OpenAI `{error:{message,type}}`, Anthropic `{type:"error",error:{type,message}}`.
  Routing failures → 400/404; upstream transport failures → 502.
- **`anthropic-version`** header defaults to `2023-06-01`, overridable per provider via `AnthropicVersion`.

## Test References

- **L0** `Domain.UnitTest/Routing` — matcher, dialect parser.
- **L0** `Application.UnitTest/Routing` — resolver, transformers (cache injection), router, error factory, options validator.
- **L2** `Host.IntegrationTest` — full pipeline incl. SSE passthrough and env-over-appsettings override (in-process stub upstream).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-14 | Initial routing feature: same-dialect router, config-driven imposters, per-dialect caching, SSE streaming. | — |
| 2026-06-14 | Moved full LADRs + C4/flow/sequence diagrams to HLD 001; trimmed this file to minimal AI-coder context. | — |
| 2026-06-15 | HLD 001 split into `README.md` index + `diagrams/`, `nfrs/`, `ladrs/` subfolders. | — |
