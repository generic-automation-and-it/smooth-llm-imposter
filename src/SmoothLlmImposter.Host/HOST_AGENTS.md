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
  deliberately unmapped because it's dialect-ambiguous. Routing/transform semantics live in Application — see
  `Features/Routing/ROUTING_AGENTS.md`.
- An un-routed request returns `404` (and a body-less request with no dialect prefix has no model to route).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-05-30 | Created — minimal runnable Host (`Program.cs`, `appsettings(.Development).json`, `Properties/launchSettings.json`) with empty `Configuration/`, `Endpoints/`, `HealthChecks/`, `Workers/`. | — |
| 2026-06-19 | Documented the dialect-prefixed routing endpoints (`/openai/**`, `/anthropic/**`, any method) + retained legacy `POST /v1/*`; corrected stale "bare bootstrap" note. | — |
