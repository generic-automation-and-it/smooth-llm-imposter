# HOST_AGENTS.md

## TL;DR

ASP.NET Core composition root (Minimal API). Wires the application together and exposes endpoints — it holds no business logic.

## Non-Negotiables

- **Keep business logic out of Host.** Endpoints translate HTTP to a Mediator request and back; they contain no domain or orchestration logic.
- **One endpoint per use case** under `Endpoints/`; cross-cutting composition (DI, middleware, observability, problem-details) lives in `Configuration/`.
- **`Program` ends with `public partial class Program { }`** so integration tests can target it via `WebApplicationFactory<Program>`.
- **References Application, Domain, and Infrastructure** — it is the only project that composes all layers.

## Key Behaviors

- **Routing endpoints (`Endpoints/RoutingEndpoints.cs`)** expose the proxy as dialect-prefixed catch-alls:
  `/openai/{**path}` and `/anthropic/{**path}` (any HTTP method). The prefix selects the `ApiDialect`; the Host
  strips it and forwards `{path}` verbatim with the inbound method, so model-discovery (`GET /v1/models`),
  `/v1/responses`, and `count_tokens` all proxy without per-endpoint mappings. Legacy unprefixed
  `POST /v1/chat/completions|/v1/responses|/v1/messages` stay mapped for back-compat; unprefixed `/v1/models` is
  deliberately unmapped because it's dialect-ambiguous. For matched OpenAI imposter routes whose provider sets
  `OpenAiUpstreamApi: chat_completions`, the Host overrides an inbound `/responses` upstream path to
  `/v1/chat/completions`; the body conversion lives in Application. Routing/transform semantics live in
  Application — see `Features/Routing/ROUTING_AGENTS.md`.
- An un-routed request returns `404` (and a body-less request with no dialect prefix has no model to route).
- Compose/runbook env vars must mirror the concrete `Imposter:Providers` indexes in `appsettings.json`.
  ASP.NET Core config binding treats a sparse env var such as `Imposter__Providers__5__ApiKey` as a sixth
  provider; if only indexes `0..4` exist in JSON, startup validation fails because that created provider has no
  `Name`, `Dialect`, or `BaseUrl`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-05-30 | Created — minimal runnable Host (`Program.cs`, `appsettings(.Development).json`, `Properties/launchSettings.json`) with empty `Configuration/`, `Endpoints/`, `HealthChecks/`, `Workers/`. | — |
| 2026-06-19 | Documented the dialect-prefixed routing endpoints (`/openai/**`, `/anthropic/**`, any method) + retained legacy `POST /v1/*`; corrected stale "bare bootstrap" note. | — |
| 2026-06-20 | Documented that compose/runbook `Imposter__Providers__N__*` env vars must not reference sparse provider indexes because they create empty providers during binding. | — |
| 2026-06-20 | Documented `OpenAiUpstreamApi: chat_completions` path override for matched OpenAI imposter routes. | — |
