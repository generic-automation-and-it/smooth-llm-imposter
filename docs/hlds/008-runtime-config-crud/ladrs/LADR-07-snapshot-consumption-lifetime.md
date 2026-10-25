# LADR-07: Read the Runtime Registry from a Scoped Catalog

**Status:** Accepted

<!-- Tactical: the *how* behind LADR-01. -->

## Context

[LADR-01](./LADR-01-runtime-mutable-registry.md) requires runtime mutations to be visible to routing without
a redeploy. Today the catalog that materialises configuration into immutable routes is a **process-lifetime
singleton built once** from `IOptions`, and the resolver and model responders that depend on it are
singletons too. A singleton can never observe a later mutation. The original Phase 1 implementation used
`IOptionsSnapshot`, but that forced the reflection configuration binder onto every request scope.

## Decision

**Read** the current `IProviderRegistry` snapshot from `ProviderCatalog` and **rebuild the catalog within the
request scope** so each inbound request resolves against the current registry. The components on the
resolution path that hold the catalog adopt a per-request view (scoped lifetime, or a per-request read)
instead of caching at process start. The startup `IOptions<ImposterOptions>` path remains only for binding,
post-configuring, validating, and seeding the registry; after seeding, runtime writes are reflected on the
next scope without re-running the configuration binder.

## Alternatives Considered

- **`IOptionsSnapshot` overlay per request** — originally implemented; rejected after production crash
  analysis because it re-runs reflection binding for the full provider dictionary on each request even though
  the registry already owns the runtime state.
- **`IOptionsMonitor` + `OnChange` invalidation of the singleton catalog** — viable, but the scoped model is
  simpler to reason about (no change-token plumbing or cache invalidation race).
- **Keep singletons, mutate the catalog in place with locks** — rejected: bespoke concurrency and
  process-lifetime catalog state would make next-request visibility harder to reason about.

## Consequences

- Mutations are visible on the next request with no invalidation step (satisfies
  [NFR-01](../nfrs/NFR-01-mutation-visibility.md)).
- Catalog/resolver lifetimes change from singleton to scoped (or per-request read); their dependents must be
  reviewed for lifetime-capture issues.
- A small per-request cost to rebuild the catalog view from a deep-cloned registry snapshot — bounded by the
  provider count (typically a handful); acceptable for a config-resolution step and well off the streaming
  body path.

## Open

- Whether to memoise the materialised catalog per scope vs per registry-version to avoid rebuilding on every
  request in the same scope — owner: implementation review; trigger: Phase 1 perf check against
  [NFR-01](../nfrs/NFR-01-mutation-visibility.md).

## Related

- **LADR-01** — the strategic decision this implements.
- **LADR-04** — the env seed runs at registry init, not as a per-request post-configure.
