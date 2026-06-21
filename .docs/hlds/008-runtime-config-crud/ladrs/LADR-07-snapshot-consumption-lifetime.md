# LADR-07: Consume Options via `IOptionsSnapshot`; Rebuild the Catalog per Request Scope

**Status:** Accepted

<!-- Tactical: the *how* behind LADR-01. -->

## Context

[LADR-01](./LADR-01-runtime-mutable-registry.md) requires runtime mutations to be visible to routing without
a redeploy. Today the catalog that materialises configuration into immutable routes is a **process-lifetime
singleton built once** from `IOptions`, and the resolver and model responders that depend on it are
singletons too. A singleton can never observe a later mutation. The issue explicitly asks to migrate
configuration consumption to `IOptionsSnapshot`.

## Decision

**Consume** configuration through `IOptionsSnapshot<ImposterOptions>` (which is **scoped** — re-evaluated per
request scope) and **rebuild the catalog within the request scope** so each inbound request resolves against
the current registry. The components on the resolution path that hold the catalog adopt a per-request view
(scoped lifetime, or a per-request read) instead of caching at process start. The configure step that
populates the snapshot reads from the in-memory registry (LADR-01) and applies the startup env seed
(LADR-04) only at initialisation, so runtime writes are reflected on the next scope.

## Alternatives Considered

- **`IOptionsMonitor` + `OnChange` invalidation of the singleton catalog** — viable, but the issue names
  `IOptionsSnapshot` and the scoped model is simpler to reason about (no change-token plumbing or cache
  invalidation race).
- **Keep singletons, mutate the catalog in place with locks** — rejected: bespoke concurrency, bypasses the
  options pipeline, and diverges from the requested `IOptionsSnapshot` migration.

## Consequences

- Mutations are visible on the next request with no invalidation step (satisfies
  [NFR-01](../nfrs/NFR-01-mutation-visibility.md)).
- Catalog/resolver lifetimes change from singleton to scoped (or per-request read); their dependents must be
  reviewed for lifetime-capture issues.
- A small per-request cost to rebuild the catalog view from the snapshot — bounded by the provider count
  (typically a handful); acceptable for a config-resolution step and well off the streaming body path.

## Open

- Whether to memoise the materialised catalog per scope vs per registry-version to avoid rebuilding on every
  request in the same scope — owner: implementation review; trigger: Phase 1 perf check against
  [NFR-01](../nfrs/NFR-01-mutation-visibility.md).

## Related

- **LADR-01** — the strategic decision this implements.
- **LADR-04** — the env seed runs at registry init, not as a per-request post-configure.
