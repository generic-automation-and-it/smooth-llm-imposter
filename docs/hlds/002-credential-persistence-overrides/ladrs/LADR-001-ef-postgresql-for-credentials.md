# LADR-001 — Reintroduce EF Core + PostgreSQL for credentials

- **Date / Status:** 2026-06-15 · Superseded by [HLD 008 LADR-05](../../008-runtime-config-crud/ladrs/LADR-05-settings-backed-provider-keyed-credentials.md)
- **Supersedes:** [HLD 001 LADR-002 — Stateless, no EF Core / PostgreSQL](../../001-llm-imposter-routing/ladrs/LADR-002-stateless-no-ef-postgresql.md)

> **Superseded by [HLD 008 LADR-05](../../008-runtime-config-crud/ladrs/LADR-05-settings-backed-provider-keyed-credentials.md).**
> The database is no longer mandatory: credentials default to a settings-backed in-memory store, and the
> EF Core + PostgreSQL backend described here becomes an **opt-in** alternative (encryption-at-rest per
> [LADR-003](LADR-003-idataprotector-secret-encryption.md) still applies to that backend). Kept for history.

## Context

HLD 001 removed all persistence to make "stores nothing, especially not keys" the differentiator from the
Smooth Claude Proxy. The new requirement — runtime-switchable, dialect-separated passthrough credentials —
cannot be met by config/env alone without redeploys. The template originally shipped EF Core + Npgsql, so
the stack is re-addable rather than novel.

## Decision

Reintroduce EF Core + PostgreSQL, scoped to a **single bounded concern**: stored passthrough credentials
(`ProviderCredential` TPH). No other state is persisted. The routing pipeline remains stateless; only the
passthrough credential lookup and the admin API touch the database.

Alternatives rejected: a flat JSON/secret file (no concurrent-safe runtime mutation, no query/audit story);
a key-vault-only approach (heavier operational dependency than needed for v1 — left as a future `IDataProtector`/
store seam, see [LADR-003](LADR-003-idataprotector-secret-encryption.md)).

## Consequences

- `Persistence/` returns: `DbContext`, TPH configuration, design-time factory, migrations
  (each `[ExcludeFromCodeCoverage]`), repository/store.
- A PostgreSQL dependency returns to local run, compose, and CI. HLD 001's "infra-free / WireMock-only"
  test posture changes: credential-resolution integration tests need a real Postgres (or a provider-faithful
  substitute); the imposter path keeps its in-process transport stub ([HLD 001 LADR-004](../../001-llm-imposter-routing/ladrs/LADR-004-in-process-transport-stub.md)).
- HLD 001 LADR-002 is **superseded** for the credential concern; it remains the rationale record for why the
  imposter path stays stateless.
