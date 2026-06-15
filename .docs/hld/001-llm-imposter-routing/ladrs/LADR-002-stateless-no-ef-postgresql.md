# LADR-002 — Stateless, no EF Core / PostgreSQL

- **Date / Status:** 2026-06-14 · Accepted

## Context

The core differentiator from the Smooth Claude Proxy is "stores nothing, especially not keys".
The template shipped an EF/Npgsql/Respawn/Aspire stack.

## Decision

Remove all persistence and the DB-backed test stack. Keys live only in config/env.

## Consequences

No `Persistence/`, no migrations, no DB component tests. Usage tracking / auditing, if ever
needed, would be a new additive decision.
