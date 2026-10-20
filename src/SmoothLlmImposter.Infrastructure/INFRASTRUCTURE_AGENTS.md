# INFRASTRUCTURE_AGENTS.md

## TL;DR

Implements the contracts defined in Application — EF Core + PostgreSQL persistence (`Persistence/`), the feature-scoped GitHub Copilot SQLite store (`Persistence/GithubCopilot/`, direct `Microsoft.Data.Sqlite`), and external HTTP clients (`Clients/`).

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

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-20 | GitHub Copilot feature store: direct Microsoft.Data.Sqlite with parameterized ADO.NET (spike rejected Dapper.AOT — no .NET 10 interceptors on net10.0, so Dapper would run on reflection/ref-emit). Implements the Application-owned `IGithubCopilotConnectionStore`; feature-gated startup bootstrap opens no database while `ENABLE_GITHUB_COPILOT` is off. | `DependencyInjection`, `Persistence/GithubCopilot` (#126) |
| 2026-09-22 | Per-attempt header timeouts derive from a per-provider base scaled ×1/×2/×3; global base 200s → 300s, so the default ladder is 300s/600s/900s. Base is carried per request (`UpstreamTimeoutSecondsKey`), not per client. | `DependencyInjection`, `UpstreamForwarder` (HLD 012) |
| 2026-08-30 | Upstream pre-header resilience uses 200s, 600s, and 900s attempt timeouts, with two retries after 1s and 2s. *(Superseded 2026-09-22.)* | `DependencyInjection` |
| 2026-05-30 | Created — empty persistence + clients skeleton (`Clients/`, `Extensions/`, `Persistence/{Configurations,Entities,Migrations,Repositories,Stores,Extensions,DesignTime}/`). | — |
