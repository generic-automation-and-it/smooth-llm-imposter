# LADR-04: Runtime CRUD Wins; Environment Overrides Seed at Startup

**Status:** Accepted

## Context

HLD 007 establishes a precedence chain for static configuration: conventional env (`<NAME>_<FIELD>`) >
structured env > `appsettings`. Once runtime CRUD exists, a new question arises: when an operator `PUT`s a
new value for a field that is also pinned by an environment variable, which one is authoritative? If env
"re-asserted" on every request, runtime edits to env-pinned fields would silently revert — surprising for an
API explicitly built to change things "on the fly".

## Decision

**Make runtime CRUD authoritative.** Environment and `appsettings` values **seed** the in-memory registry
**once at startup** (preserving the HLD 007 precedence among the static sources). After seeding, the admin
API is the sole authority until the process restarts; a runtime write to any field — including one originally
sourced from an env var — wins and is not re-overwritten by the env layer on subsequent requests. A restart
re-seeds from env + config, discarding runtime edits (see [NFR-04](../nfrs/NFR-04-ephemerality.md)).

## Alternatives Considered

- **Env re-asserts every request (env > runtime)** — rejected: an operator could not change an env-pinned
  field on the fly, contradicting the core intent; the behaviour would also be non-obvious.
- **Per-field "locked by env" markers** — rejected: adds operational complexity for a niche guarantee not
  requested.

## Consequences

- Operators can override any field at runtime, including env-pinned secrets/URLs — intuitive and matching the
  feature's purpose.
- Environment remains the **baseline/bootstrap** mechanism; "what env says" and "what is live" can diverge
  until restart — this must be documented so operators are not surprised after a redeploy resets edits.
- The env-seed must run during registry initialisation, not as a per-request post-configure that fights the
  runtime layer.

## Related

- **LADR-01** — the registry these values seed and that runtime writes mutate.
- Refines HLD 007 [LADR-02](../../007-named-provider-env-overrides/ladrs/LADR-02-conventional-env-surface.md)
  (conventional env surface) by scoping it to startup seeding.
