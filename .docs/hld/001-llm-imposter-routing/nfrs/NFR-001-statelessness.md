# NFR-001 — Statelessness

- **Category:** Scalability / Operability
- **Status:** Accepted · 2026-06-14

The router holds no per-request or persistent state — no database, cache store, or session
affinity. Any instance can serve any request, so the service scales horizontally and restarts
with no data-loss concerns.

See [LADR-002 — Stateless, no EF Core / PostgreSQL](../ladrs/LADR-002-stateless-no-ef-postgresql.md).
