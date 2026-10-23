# NFR-001 — Statelessness

- **Category:** Scalability / Operability
- **Status:** Accepted · 2026-06-14

Default inference routing needs no persistent state or session affinity. Any instance can serve any
request, so the service scales horizontally and restarts without losing inference correctness.

Opt-in features may hold bounded, disposable process memory: HLD 014's classifier memo and
[LADR-008's first-seen tool ranks](../ladrs/LADR-008-cache-friendly-tool-ordering.md) are optimizations,
not upstream caches. Expiry, eviction, restart or another replica can reset ordering/cache performance;
they must never retain tool definitions or make request correctness depend on affinity. Credential
state for the separately enabled Copilot/admin features follows its own HLD contracts.

See [LADR-002 — Stateless, no EF Core / PostgreSQL](../ladrs/LADR-002-stateless-no-ef-postgresql.md).
