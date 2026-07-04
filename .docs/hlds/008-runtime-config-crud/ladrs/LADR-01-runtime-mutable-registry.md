# LADR-01: Runtime-Mutable Provider Registry Read by Scoped Routing Consumers

**Status:** Accepted

## Context

HLD 007 keys providers by name in an `Imposter:Providers` dictionary, but that dictionary is bound once at
startup and materialised into a process-lifetime catalog that is never re-read. Issue #48 requires operators
to insert, update, get, and delete providers **on the fly** without a redeploy, and explicitly **without
persistence**. We need a configuration source of truth that can change at runtime yet still boots from the
same `appsettings` + environment baseline the project already supports.

## Decision

**Adopt** a runtime-mutable, in-memory provider **registry** as the source of truth for routing
configuration, exposed to the routing path through scoped catalog consumers. The registry is **seeded once at
startup** from the bound configuration (the HLD 007 baseline, including environment overrides) and is then
mutated only by the admin API. It is **never persisted** — a process restart deterministically reseeds it
from config + env. Reads on the routing path observe the registry's current contents per request scope (see
[LADR-07](./LADR-07-snapshot-consumption-lifetime.md)).

## Alternatives Considered

- **Persist the registry (DB or file)** — rejected: the issue mandates "not persisted"; persistence
  reintroduces state, migrations, and a restart-divergence between config and store.
- **`IOptionsMonitor` change-token reload from a file/config provider** — rejected: it couples runtime edits
  to writing config files and re-binding, which is heavier than an in-memory write and muddies the
  "not persisted" requirement.
- **Mutate the singleton catalog directly** — rejected: a process-lifetime materialised catalog can never
  naturally observe later registry changes, and mutating it in place would force bespoke thread-safety.

## Consequences

- Operators get true on-the-fly CRUD with no redeploy and no database.
- Restart is a deliberate "reset to configured baseline" — operators must re-apply runtime-only changes
  (documented behaviour, see [NFR-04](../nfrs/NFR-04-ephemerality.md)).
- The catalog can no longer be a once-built singleton; its lifetime changes (LADR-07).
- The registry needs thread-safe mutation (concurrent admin writes vs request reads).

## Amendment

2026-07-04: the original implementation exposed the registry to scoped consumers by re-binding
`IOptionsSnapshot<ImposterOptions>` on every request. That preserved mutation visibility but put the
reflection configuration binder on the hot path. The amended implementation keeps the runtime registry and
scoped catalog lifetime, but `ProviderCatalog` reads `IProviderRegistry.Snapshot()` directly after startup
seeding, falling back to cached `IOptions<ImposterOptions>` only before the registry is seeded.

## Open

- A future opt-in snapshot-to-disk export of the in-memory registry. **Out of scope for this HLD** — it would
  contradict [NFR-04](../nfrs/NFR-04-ephemerality.md) ("no file or database is written by the runtime
  provider-config registry") and the AGENTS.md Non-Negotiable, so it must not be read as an option within the
  current design. Track in a follow-up HLD that explicitly supersedes NFR-04 if pursued — owner: design review
  on #48; trigger: operator feedback after Phase 1.

## Related

- **LADR-04** — defines how the startup env seed relates to later runtime writes.
- **LADR-07** — the tactical consumption/lifetime mechanism that makes mutations visible.
- Refines HLD 007 (named-provider dictionary) by making it mutable.
