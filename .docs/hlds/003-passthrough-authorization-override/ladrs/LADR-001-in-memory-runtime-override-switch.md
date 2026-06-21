# LADR-001: Override state is an in-memory switch — never persisted

**Status:** Accepted

<!-- Status lifecycle: Draft → Prototype → Accepted. -->

## Context

The operator wants a *runtime trigger* they can flip from a terminal, not a configuration value that
survives deployments. A switch that persisted across restarts would silently keep forcing `Bearer` after a
process recycle — surprising on the auth-sensitive forwarding path — and would require a new persisted
column or table. HLD 002 already owns all persisted credential state; this feature should add no schema.

## Decision

**Hold** the override as in-memory, per-`(dialect, provider)` boolean state owned by a single
application-scoped service, defaulting to OFF and resetting to OFF on process start. There is exactly one
flag per `(dialect, provider)`; the dialect-only `/routing/{dialect}/override-authorization` route targets
the dialect's **enabled default** provider (HLD 008 LADR-06). No row, column, or migration is added. `PUT`
sets a `(dialect, provider)`'s flag true, `DELETE` sets it false, and a read returns the current value — all
against process memory. Provider keys compare case-insensitively, consistent with the credential stores.

Because the routing pipeline is otherwise stateless (HLD 001), this is the only mutable in-process state on
the request path, and it is a single boolean per `(dialect, provider)` read with no I/O.

## Alternatives Considered

- **Persist the switch in PostgreSQL** — rejected: survives restarts (operator explicitly wanted a runtime trigger that a restart forgets), and adds a migration for a single boolean.
- **Configuration / environment flag** — rejected: changing it needs a redeploy/restart, defeating "flip it with a curl".
- **Reuse `IsActive` as the switch** — rejected: `IsActive` selects *which* credential is used (HLD 002); conflating it with "force Bearer" would overload one flag with two orthogonal meanings.

## Consequences

- A restart is a fail-safe reset to OFF (see [NFR-003](../nfrs/NFR-003-toggle-observability-operability.md)).
- Multi-instance deployments do **not** share switch state — each instance is toggled independently. Acceptable for v1 (single-instance operator tool); flagged as a known limitation, not a defect.
- No new migration, entity, or `[ExcludeFromCodeCoverage]` persistence code.

## Related

- **LADR-002** — what the switch, when ON, changes about forwarding.
- **LADR-005** — the guard that refuses to arm the switch with no active credential.
