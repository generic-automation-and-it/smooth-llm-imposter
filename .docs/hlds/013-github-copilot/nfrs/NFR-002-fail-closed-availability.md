# NFR-002: Fail closed on unusable credentials; corrupt storage is actionable

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-20 |

## Requirement

- A matched Copilot route with no usable connection fails closed (503) — it never borrows caller auth, a config
  secret or an older token pair.
- An expired, revoked, refresh-failed or undecryptable connection marks the revision reauthentication-required
  and retains the durable row; routing stays unavailable until a new login.
- A corrupt, unwritable or newer-schema store produces an actionable startup/login failure rather than resetting
  or silently dropping data.
- Network failures do not grant entitlement; malformed entitlement or a non-GitHub origin is not a successful
  setup.
- Inference performs no SQLite or GitHub lookup; the published snapshot is the only read.
- SSE bodies keep their unlimited lifetime and the existing header-only timeout ladder is unchanged.

## Verification

L0 covers the state transitions with a manual clock; L1 covers rollback, reopen and key-ring failure; L2 covers
401 invalidation, reauthentication status and fail-closed follow-up requests; the native run covers startup with
an empty and a populated store.
