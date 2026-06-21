# NFR-04: Ephemerality / Restart Semantics (Operability)

**Status:** Draft

## Requirement

Runtime mutations are **in-memory only**. A process restart deterministically reseeds the registry from
configuration + environment (the HLD 007 baseline), discarding all runtime-only edits. There is no
persistence of the runtime registry and no hidden state surviving a restart.

## Verification

Integration test (L2): mutate the registry via the admin API, then construct a fresh host instance with the
same config/env and assert the registry matches the configured baseline (runtime edits are gone). A
no-database test asserts CRUD works and is lost on restart with no connection string present.

## Acceptance Criteria

- After restart, `GET`-ing a provider returns the configured baseline, not the last runtime value.
- Two hosts booted from identical config + env expose identical registries (deterministic seeding).
- No file or database is written by the runtime provider-config registry.

## Applies To

Goal 1; the registry seeding and lifetime
([LADR-01](../ladrs/LADR-01-runtime-mutable-registry.md), [LADR-04](../ladrs/LADR-04-runtime-wins-over-env.md)).
Preserves HLD 001 statelessness for the provider-config registry (the optional credential database backend is
the only persisted state, opt-in per [LADR-05](../ladrs/LADR-05-settings-backed-provider-keyed-credentials.md)).
