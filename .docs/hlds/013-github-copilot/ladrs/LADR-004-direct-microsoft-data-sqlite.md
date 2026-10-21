# LADR-004: Persistence is direct Microsoft.Data.Sqlite, not Dapper or an ORM

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-20 |
| **Deciders** | SmoothLlmImposter maintainers |

## Context

NativeAOT is the deployment baseline. Dapper.AOT was spiked for this feature and rejected: version 1.1.0 emits
no .NET 10 interceptors (its compile asset is a runtime-only `_._` placeholder and `[module: DapperAot]` is not
available at compile time), so Dapper silently falls back to reflection/ref-emit — the exact path AOT forbids.
EF Core is JIT-only in this repo and already fail-fasts under AOT for the optional PostgreSQL backend.

## Decision

The feature store uses `Microsoft.Data.Sqlite` directly: parameterized ADO.NET commands, explicit row mapping
and an explicitly versioned schema. No Dapper, no EF Core, no generic repository framework on this path. Outbound
GitHub JSON uses source-generated metadata or `JsonDocument`.

## Consequences

- The store is verbose; every query and mapping is hand-written, and schema changes need a migration step in the
  same class.
- The path is provably trim/AOT-safe and was exercised by an AOT publish plus a native run of the login/admin
  endpoints.
- Native SQLite assets must ship for Linux musl amd64/arm64; the container release gate verifies both.
