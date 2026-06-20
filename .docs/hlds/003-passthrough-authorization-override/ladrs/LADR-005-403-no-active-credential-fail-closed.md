# LADR-005: `403` when arming with no active credential; fail closed at request time

**Status:** Draft

<!-- Tactical: the no-credential behaviour, at toggle time and at request time. -->

## Context

The override forces `Bearer` from the dialect's **active** credential. If there is no active credential there
is nothing to present. The operator asked that "if there is no auth key, a `401`/`403` is returned." Two
distinct moments can hit this: arming the switch (`PUT`) when no active credential exists, and a passthrough
request arriving while the override is ON but the active credential has since been removed.

## Decision

**Refuse to arm and fail closed.**

- **At toggle time** — `PUT …/override-authorization` returns **`403`** when the dialect has no active stored
  credential, and the switch stays OFF. The caller is an authenticated admin, so `401` (no/invalid caller
  auth) is semantically wrong; `403` reads as "understood, but there is no upstream authorization to
  activate." (`409 Conflict` was considered as more strictly REST-correct, but `403` matches the operator's
  stated `401`/`403` expectation and the authorization framing.)
- **At request time** — if the override is ON but no active credential resolves, the passthrough request
  **fails closed** with a dialect-shaped auth error rather than reverting to `x-api-key`/the config key.
  Silently falling back would re-emit the very header the override exists to suppress.

## Alternatives Considered

- **`401` on arm** — rejected: the admin caller is authenticated; `401` implies the *caller's* auth failed.
- **`409 Conflict` on arm** — reasonable REST alternative; not chosen, to honour the operator's `401`/`403` ask.
- **At request time, fall back to `x-api-key`/config key** — rejected: defeats the override's purpose and silently leaks the suppressed scheme.

## Consequences

- You cannot enable an override that has nothing to use; the failure is visible at the moment you try to arm it.
- An armed override whose credential is later deleted degrades to a clear auth error, not a silent scheme change.
- The `403` body is admin-API-shaped (not a dialect proxy error), since the `PUT`/`DELETE` endpoints are an admin surface.

## Related

- **LADR-002** — why a missing active credential leaves nothing to present.
- **NFR-001** — the guarantee that `x-api-key` is never emitted while the override is ON.
