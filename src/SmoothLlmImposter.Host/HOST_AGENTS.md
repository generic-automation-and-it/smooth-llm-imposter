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
  `/v1/chat/completions`; the body conversion lives in Application. That same downgraded path translates the
  upstream Chat Completions response back to Responses shape before writing to the caller. Routing/transform
  semantics live in Application — see `Features/Routing/ROUTING_AGENTS.md`.
- An un-routed request returns `404` (and a body-less request with no dialect prefix has no model to route).
- **Codex CLI client contract.** Codex drives the proxy through a `~/.codex/config.toml` `model_provider` whose
  `base_url` is the router root **plus the `/openai` dialect prefix** (Codex's Responses client appends
  `/responses`), with `wire_api = "responses"` and `requires_openai_auth = true` (Codex then sends its
  ChatGPT/subscription auth, which the router forwards on passthrough or replaces on a matched imposter route).
  `wire_api` stays `responses` even when the matched upstream speaks `chat_completions` — the Host bridges
  Responses↔Chat (above). The run-mode setup docs under `.docs/wiki/setups/` write this file with that mode's
  published port; keep the port and prefix aligned there (root `AGENTS.md` carries this drift rule).
- **Logging is config-driven.** `Program.cs` sets a baseline `MinimumLevel.Information()` then layers
  `ReadFrom.Configuration` last, so the `Serilog` section in `appsettings.json` / env vars overrides it (the old
  `Logging` section was dead config — Serilog never read it). `RoutingEndpoints` logs a **Debug** full-inbound-request
  dump (method, path, query, all headers, raw body) under the `SmoothLlmImposter.Routing` category, guarded by
  `IsEnabled(Debug)` so it is free when off. `Authorization`/`x-api-key` values are masked (scheme + last 4). Enable
  with `Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug`. See
  `.docs/wiki/setups/logging.debug-smooth-llm-imposter.md`.
- Compose/runbook env vars must mirror the concrete `Imposter:Providers` indexes in `appsettings.json`.
  ASP.NET Core config binding treats a sparse env var such as `Imposter__Providers__5__Secret` as a sixth
  provider; if only indexes `0..4` exist in JSON, startup validation fails because that created provider has no
  `Name`, `Dialect`, or `BaseUrl`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-05-30 | Created — minimal runnable Host (`Program.cs`, `appsettings(.Development).json`, `Properties/launchSettings.json`) with empty `Configuration/`, `Endpoints/`, `HealthChecks/`, `Workers/`. | — |
| 2026-06-19 | Documented the dialect-prefixed routing endpoints (`/openai/**`, `/anthropic/**`, any method) + retained legacy `POST /v1/*`; corrected stale "bare bootstrap" note. | — |
| 2026-06-20 | Documented that compose/runbook `Imposter__Providers__N__*` env vars must not reference sparse provider indexes because they create empty providers during binding. | — |
| 2026-06-20 | Documented `OpenAiUpstreamApi: chat_completions` path override for matched OpenAI imposter routes. | — |
| 2026-06-20 | Made Serilog level config-driven (`ReadFrom.Configuration`; replaced dead `Logging` section with `Serilog`) and added the `Debug` full-inbound-request dump (auth-masked) on the `SmoothLlmImposter.Routing` category. | — |
| 2026-06-20 | Wired the scoped `/responses`→Chat response bridge: translated Chat SSE/non-streaming responses are written back as Responses events/objects, while all other responses keep the existing byte-copy path. | HLD 004 LADR-05 |
| 2026-06-20 | Documented the Codex CLI client contract (`~/.codex/config.toml` `model_provider`: `/openai`-prefixed `base_url`, `wire_api = "responses"`, `requires_openai_auth = true`); the Conductor.Build setup script now writes this file with the mode's published port. | #26 |
