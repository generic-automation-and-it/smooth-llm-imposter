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
  `IsEnabled(Debug)` so it is free when off. The inbound dump masks only the standard `Authorization`/`x-api-key`
  headers (routing — and therefore the resolved provider's `AuthHeader` — has not happened yet); a provider-specific
  `AuthHeader` (e.g. `api-key`) is masked only in the **forwarder's outbound** dump. So a caller that sends its
  credential in a non-standard inbound header is not masked here — a known Debug-only gap. Enable
  with `Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug`. See
  `.docs/wiki/setups/logging.debug-smooth-llm-imposter.md`.
- **Providers are name-keyed, not positional (HLD 007).** `ImposterOptions.Providers` is a
  `Dictionary<string, ProviderOptions>`, so an override is addressed by provider name
  (`Imposter__Providers__opencode-go-openai__Secret`) or the conventional surface (`OPENCODE_GO_API_KEY`, which wins)
  and survives any reordering — there is no `__<index>__` addressing. A legacy JSON **array** binds as numeric
  keys (`"0"`,`"1"`,…); the validator rejects that (and case-only-duplicate keys) at startup with a message
  naming the `Providers: { "<name>": { ... } }` shape, so an un-migrated config fails fast rather than binding
  silently. The conventional resolver runs as an `IPostConfigureOptions` in Application (Host only binds).
- **Runtime provider-config admin endpoints (HLD 008 Phase 1).** `Endpoints/ProviderConfigurationEndpoints.cs`
  maps `/admin/providers` and requires the existing `CredentialAdmin` policy. Endpoints are thin HTTP-to-Mediator
  adapters only; the Application slices own validation, registry mutation, secret preservation, and enable/disable
  semantics. This boundary is secret-free — do not add `Secret` to Host request/response bodies here.

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
| 2026-06-20 | HLD 007: providers are now name-keyed (`Dictionary<string, ProviderOptions>`); supersedes the positional `__N__` sparse-index note — overrides are name-addressed (`Imposter__Providers__<name>__*`) or conventional (`<NAME>_API_KEY`, precedence-winning), and a legacy array/numeric-key shape fails fast at startup. | HLD 007 |
| 2026-06-20 | Default config uses dialect-suffixed provider keys: `opencode-go-openai` / `opencode-go-anthropic` share `OPENCODE_GO_API_KEY`, and `openrouter-openai` / `openrouter-anthropic` share `OPENROUTER_API_KEY`; OpenRouter routes use Bearer auth even on the Anthropic-compatible surface. | — |
| 2026-06-21 | Added `/admin/providers` runtime provider-config CRUD plus enable/disable. Host maps the secret-free admin surface and delegates all behavior to Mediator/Application. | #49 |
| 2026-07-02 | Documented the optional `AuthHeader` override (relocates the credential to a non-standard header, e.g. an `api-key` gateway; value format still follows `AuthScheme`). The custom header is masked only in the forwarder's **outbound** Debug dump; the inbound dump masks just `Authorization`/`x-api-key` (routing hasn't resolved the provider yet) — a known Debug-only gap for callers sending a non-standard auth header inbound. | — |
