# LADR-002 — Stateless, no EF Core / PostgreSQL

- **Date / Status:** 2026-06-14 · Accepted · **Superseded (2026-06-15)** for the credential concern by
  [HLD 002 LADR-001](../../002-credential-persistence-overrides/ladrs/LADR-001-ef-postgresql-for-credentials.md)

> **Superseded for stored credentials.** HLD 002 reintroduces EF Core + PostgreSQL **only** for persisted
> passthrough credentials. The imposter routing path remains stateless as described below — this record
> stays as the rationale for why the hot path stores nothing.

## Context

The core differentiator from the Smooth Claude Proxy is "stores nothing, especially not keys".
The template shipped an EF/Npgsql/Respawn/Aspire stack.

## Decision

Remove all persistence and the DB-backed test stack. Keys live only in config/env.

## Consequences

No `Persistence/`, no migrations, no DB component tests. Usage tracking / auditing, if ever
needed, would be a new additive decision.
