# Infrastructure — Layer Context

## TL;DR

Implements the contracts defined in Application — EF Core + PostgreSQL persistence (`Persistence/`), the feature-scoped GitHub Copilot SQLite store (`Persistence/GithubCopilot/`, direct `Microsoft.Data.Sqlite`), its isolated Data Protection key ring, the device-flow OAuth and Copilot model clients (`Clients/GithubCopilot/`), and external HTTP clients (`Clients/`).

## Non-Negotiables

- **Implements Application interfaces; never the reverse.** Concrete stores/clients here implement `IFoo` from Application. Application must not reference an Infrastructure concrete type.
- **References Application and Domain only** — never Host.
- **Keep upstream streaming transport infinite at the `HttpClient` layer.** Time-bound outbound LLM calls with
  Polly attempt timeouts inside the resilience pipeline so `ResponseHeadersRead` retries/timeouts apply only
  before headers arrive; do not cap SSE body streaming with `HttpClient.Timeout`.
- **The per-attempt timeout base travels on the request, not on the client (HLD 012).** One named client
  (`imposter-upstream`) serves every provider, and providers are runtime-mutable (HLD 008), so a client per
  provider cannot work. `UpstreamForwarder` stamps `DependencyInjection.UpstreamTimeoutSecondsKey` on the
  outbound `HttpRequestMessage` and the Polly `TimeoutGenerator` reads it back via
  `context.GetRequestMessage()`; unstamped ⇒ `DefaultUpstreamTimeoutSeconds` (300). **That stamp is the
  load-bearing hop** — without it the option is configurable end-to-end and completely inert, so it is
  covered from both sides (`UpstreamForwarderTimeoutTests`) and through the real pipeline
  (`UpstreamTimeoutPipelineTests`). Never let a non-positive base reach Polly: `0` is not "no timeout", it
  fails every attempt instantly.
- **EF Core migrations are generated code.** Keep them under `Persistence/Migrations/`; they are marked generated via the root `.editorconfig` glob and generated migration classes should carry `[ExcludeFromCodeCoverage]`. Register the `DbContext` with a scoped lifetime.
- **No business rules.** Infrastructure adapts to the outside world (DB, HTTP, cache); domain decisions stay in Domain, orchestration in Application.

## Packages to add when implementing

`Microsoft.EntityFrameworkCore(.Relational/.Design/.Tools)`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Refit.HttpClientFactory`, `Microsoft.Extensions.Http.Resilience` — declared centrally in `Directory.Packages.props`.


## GitHub Copilot clients and protector (HLD 013)

- **OAuth and Copilot clients never follow a redirect** (`SocketsHttpHandler.AllowAutoRedirect = false`), are
  time-bounded, cap response size, and carry **no** automatic POST retry policy — the login/refresh state machines
  own retry decisions. Do not add a resilience handler to `github-copilot-oauth`/`github-copilot-api`.
- **GitHub JSON is source-generated or `JsonDocument`.** Adding a response DTO means registering it in
  `Clients/GithubCopilot/GithubCopilotJsonContext`.
- **The Copilot protector uses its own key ring** (`<data dir>/keys`, application name
  `smooth-llm-imposter-github-copilot`). Never route Copilot tokens through `DataProtectionSecretProtector` or the
  host's generic Data Protection registration.
- `SqliteGithubCopilotConnectionStore` is opened with a nullable connection string: while the feature is disabled
  it has none and any open attempt throws rather than creating a file.

## Decision service transport (HLD 014, HLD 015)

- The `decision-service` client is auxiliary classification, not `imposter-upstream`: its full response body
  must remain inside the attempt timeout. Reusing inference header-only resilience would leave body stalls unbounded.
- The process-wide gate is `DecisionServiceGate`, its own singleton that every client instance receives — never a
  field on the client, or a second client registration would create a second budget ([HLD 015 LADR-03](../../docs/hlds/015-shared-decision-model-integration/ladrs/LADR-03-shared-uncached-resource-budget.md)).
  The uncached exception and retry-status contract live in [HLD 014 LADR-04](../../docs/hlds/014-decisions-tools-filter/ladrs/LADR-04-singleton-gate.md).
  Do not turn the gate into per-scope state or introduce a cache during a transport refactor.
- The gate throws `DecisionGateTimeoutException` (Application contract), and the client translates the resilience
  library's timeout rejection into `DecisionServiceTimeoutException`; callers classify by these types, never by
  exception text.
- The client reads `DecisionServiceOptions`, never the routing options. The resilience profile follows
  `DecisionServiceDemand` (any consumer opted in), not a consumer's enable flag; with no consumer it uses defaults
  so unvalidated settings never reach Polly.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-28 | Added process-wide outbound HTTP concurrency gate (`ConcurrencyLimitingHandler` + shared `SemaphoreSlim`) on every HttpClient; size configurable via `Imposter:MaxConcurrentRequests` / `IMPOSTER_MAX_CONCURRENT_REQUESTS` (default 1) | — |
| 2026-10-25 | Decision exceptions: gate timeout and attempt timeout have Application contract types (LADR-11) | — |
| 2026-10-24 | Decision transport made consumer-neutral (HLD 015): `DecisionServiceGate` singleton, client reads `DecisionServiceOptions`, profile follows the any-consumer predicate; named client `decisions-tools` → `decision-service` | #156 |
| 2026-10-24 | Renamed to nested `AGENTS.md` (Claude Code auto-discovery); in-file links updated. No behavior change. | #152 |
| 2026-10-22 | Auxiliary decisions-tools client; complete-response budget and shared gate | #138 |
| 2026-10-20 | GitHub Copilot feature store: direct Microsoft.Data.Sqlite with parameterized ADO.NET (spike rejected Dapper.AOT — no .NET 10 interceptors on net10.0, so Dapper would run on reflection/ref-emit). Implements the Application-owned `IGithubCopilotConnectionStore`; feature-gated startup bootstrap opens no database while `ENABLE_GITHUB_COPILOT` is off. | `DependencyInjection`, `Persistence/GithubCopilot` (#126) |
| 2026-09-22 | Per-attempt header timeouts derive from a per-provider base scaled ×1/×2/×3; global base 200s → 300s, so the default ladder is 300s/600s/900s. Base is carried per request (`UpstreamTimeoutSecondsKey`), not per client. | `DependencyInjection`, `UpstreamForwarder` (HLD 012) |
| 2026-08-30 | Upstream pre-header resilience uses 200s, 600s, and 900s attempt timeouts, with two retries after 1s and 2s. *(Superseded 2026-09-22.)* | `DependencyInjection` |
| 2026-05-30 | Created — empty persistence + clients skeleton (`Clients/`, `Extensions/`, `Persistence/{Configurations,Entities,Migrations,Repositories,Stores,Extensions,DesignTime}/`). | — |
| 2026-10-20 | GitHub Copilot feature store `List()`, isolated Data Protection protector, device-flow OAuth client and Copilot model client; bootstrap now hydrates/publishes via the Application startup coordinator before its probe. | `Persistence/GithubCopilot`, `Clients/GithubCopilot` (#126) |
