# INFRASTRUCTURE_AGENTS.md

## TL;DR

Implements the contracts defined in Application — EF Core + PostgreSQL persistence (`Persistence/`) and external HTTP clients (`Clients/`).

## Non-Negotiables

- **Implements Application interfaces; never the reverse.** Concrete stores/clients here implement `IFoo` from Application. Application must not reference an Infrastructure concrete type.
- **References Application and Domain only** — never Host.
- **Keep upstream streaming transport infinite at the `HttpClient` layer.** Time-bound outbound LLM calls with
  Polly attempt timeouts inside the resilience pipeline so `ResponseHeadersRead` retries/timeouts apply only
  before headers arrive; do not cap SSE body streaming with `HttpClient.Timeout`.
- **EF Core migrations are generated code.** Keep them under `Persistence/Migrations/`; they are marked generated via the root `.editorconfig` glob and generated migration classes should carry `[ExcludeFromCodeCoverage]`. Register the `DbContext` with a scoped lifetime.
- **No business rules.** Infrastructure adapts to the outside world (DB, HTTP, cache); domain decisions stay in Domain, orchestration in Application.

## Packages to add when implementing

`Microsoft.EntityFrameworkCore(.Relational/.Design/.Tools)`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Refit.HttpClientFactory`, `Microsoft.Extensions.Http.Resilience` — declared centrally in `Directory.Packages.props`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-05-30 | Created — empty persistence + clients skeleton (`Clients/`, `Extensions/`, `Persistence/{Configurations,Entities,Migrations,Repositories,Stores,Extensions,DesignTime}/`). | — |
