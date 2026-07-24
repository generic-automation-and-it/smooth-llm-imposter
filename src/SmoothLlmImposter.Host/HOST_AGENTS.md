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
  upstream Chat Completions response back to Responses shape before writing to the caller. `CallerHeaders` are
  captured once and shared by route planning (HLD 009 session resolve) and the forwarder (header relay + optional
  `x-opencode-session` stamp). Routing/transform semantics live in Application — see
  `Features/Routing/ROUTING_AGENTS.md`.
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
  `IsEnabled(Debug)` so it is free when off. The inbound dump masks Authorization/x-api-key plus the session-identity capture
  headers (session_id, x-opencode-session, x-session-id, conversation_id) and the stable-identity
  headers (chatgpt-account-id, openai-organization, openai-project) — the full set is shared with the outbound dump via
  SensitiveHeaderNames so the two cannot drift. A provider-specific
  `AuthHeader` (e.g. `api-key`) is masked only in the **forwarder's outbound** dump. So a caller that sends its
  credential in a non-standard inbound header is not masked here — a known Debug-only gap. Enable
  with `Serilog__MinimumLevel__Override__SmoothLlmImposter.Routing=Debug`. See
  `.docs/wiki/setups/logging.debug-smooth-llm-imposter.md`.
- **Process-level last-resort crash logging is wired in `Program.cs`.** The Host subscribes to
  `AppDomain.CurrentDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` after `builder.Build()`
  so faults that escape request execution still get logged through DI-backed `ILogger` before process teardown.
  The unhandled-exception callback flushes Serilog synchronously with `Log.CloseAndFlush()` because the runtime
  terminates immediately afterward; the unobserved-task callback logs and calls `SetObserved()` so survivable
  background task faults do not escalate later. Keep these hooks in Host startup, and keep their logging free of
  request/business logic.
- **Providers are name-keyed, not positional (HLD 007).** `ImposterOptions.Providers` is a
  `Dictionary<string, ProviderOptions>`, so an override is addressed by provider name
  (`Imposter__Providers__opencode-go-openai__Secret`) or the conventional surface (`OPENCODE_GO_API_KEY`, which wins)
  and survives any reordering — there is no `__<index>__` addressing. A legacy JSON **array** binds as numeric
  keys (`"0"`,`"1"`,…); the validator rejects that (and case-only-duplicate keys) at startup with a message
  naming the `Providers: { "<name>": { ... } }` shape, so an un-migrated config fails fast rather than binding
  silently. The conventional resolver runs as an `IPostConfigureOptions` in Application (Host only binds).
- **Config split: base ships passthrough defaults + inert imposter templates; secret-bearing imposter config is Development-scoped.** > **TL;DR** — Both `opencode-go-*` provider templates ship **inert** in the base file: empty `Secret` and empty `Models[]`. They are present so a Production env override (`Imposter__Providers__opencode-go__Secret=...`) or `appsettings.Production.json` fragment can opt in without touching the base file. **Note**: `Models` is structured-only — there is no conventional `Imposter__Providers__<name>__Models` scalar env suffix; use the structured form `Imposter__Providers__<name>__Models__0__From=...`, the `/admin/providers` admin CRUD API, or `appsettings.Development.json`. `appsettings.json`
  (the file baked into the Release/Docker image) carries the two keyless default passthrough providers
  (`anthropic-default`, `openai-default`) **plus inert imposter-provider templates** (e.g. `opencode-go-*` —
   no populated `Secret`, no `Models`) so Production env vars can fill them in without an extra config file. The
  secret-bearing imposter providers (`*-personal`, `openrouter-*`, plus the Development-only `opencode-go-*`
  overrides that supply a real `Secret`) live in `appsettings.Development.json`, which the container never
  loads (it runs with no `ASPNETCORE_ENVIRONMENT` → Production). The csproj sets
  `<Content Update="appsettings.Development.json" CopyToPublishDirectory="Never" />` so that file is excluded from
  the publish output entirely — no dev/imposter config ships in the image. Consequences: the base config still
  satisfies `ValidateOnStart` (≥1 provider) so the container boots; in Production, imposter providers must be
  supplied via env vars (`Imposter__Providers__<name>__*`) or the `/admin/providers` runtime CRUD API. Do **not**
  populate `Secret` or `Models` on the shipped `opencode-go-*` templates — the shipped entries must stay inert;
  fill them in via env vars or in `appsettings.Development.json` only. The same `Models: []` inertness applies to
  the base `opencode-go-*` templates in `appsettings.json` — so no imposter route matches them in Production until
  the operator populates `Models` via env vars, mirroring the dev-template behaviour. Note: the dev `opencode-go-*`
  providers declare `SessionForwarding=opencode-go` but `Models: []`, so no imposter route ever matches them and the
  `SessionForwarding` field is currently inert there — a reminder that the opt-in is only meaningful alongside a
  populated `Models` array.
- **Runtime provider-config admin endpoints (HLD 008 Phase 1).** `Endpoints/ProviderConfigurationEndpoints.cs`
  maps `/admin/providers` and requires the existing `CredentialAdmin` policy. Endpoints are thin HTTP-to-Mediator
  adapters only; the Application slices own validation, registry mutation, secret preservation, and enable/disable
  semantics. This boundary is secret-free — do not add `Secret` to Host request/response bodies here.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-07-24 | `CaptureCallerHeaders` is shared with `PlanAsync` so HLD 009 session identity can be resolved without leaking `HttpContext` downstream. The local SensitiveHeaders set was removed; the inbound dump now consults the shared SensitiveHeaderNames set so the inbound/outbound masks cannot drift. | #72 |
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
| 2026-07-04 | Moved secret-bearing imposter providers (`*-personal`, `openrouter-*`, `opencode-go-*`) from `appsettings.json` to `appsettings.Development.json`; base ships only the two keyless default passthrough providers. Excluded `appsettings.Development.json` from publish output (`CopyToPublishDirectory=Never`) so the Release/Docker image carries no dev/imposter config. Stops residual model rewrites (e.g. `claude-opus-4-6 → claude-opus-4-8`) shipping in the container. | — |
| 2026-07-24 | Re-introduced `opencode-go-*` imposter templates into `appsettings.json` as **inert** entries (no populated `Secret`, no `Models`) so Production env vars can fill them without shipping a second config file. `appsettings.Development.json` keeps the Development-only overrides that supply a real `Secret`. Base config still ships no populated `Secret` and no populated `Models`, so the 2026-07-04 invariant (no secrets, no model rewrites in the Release/Docker image) is preserved. | — |
| 2026-07-06 | Documented the startup-level `UnhandledException` / `UnobservedTaskException` hooks that log last-resort process faults and flush Serilog before termination, closing the observability gap for crashes outside the request pipeline. | — |
