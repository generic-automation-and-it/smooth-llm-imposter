# NFR-003: Operability — observable toggling, zero hot-path cost, fail-safe default

**Status:** Draft

## Requirement

- **Observable:** each enable/disable is logged at `Information` recording actor, dialect, and action
  (enabled/disabled) — never the secret — per the project logging conventions. The current state is readable
  via `GET /routing/{dialect}/override-authorization`.
- **Zero hot-path cost:** reading the override flag on the passthrough path is an in-memory O(1) lookup and
  adds **no** database round-trip for the switch state itself. (The active-credential lookup it gates is the
  same one HLD 002 already performs on passthrough.)
- **Fail-safe default:** every dialect's override is OFF at process start and after restart; there is no
  persisted state to recover.

## Verification

- Log assertion: a `PUT`/`DELETE` emits one `Information` line with dialect + action and no secret.
- Test: passthrough request handling reads switch state without an added DB query attributable to the switch (the only DB read is the existing active-credential lookup).
- Test: after constructing a fresh application instance, every dialect's override reads OFF.

## Acceptance Criteria

- Toggling a dialect produces exactly one auditable, secret-free `Information` log entry.
- The switch contributes no per-request DB I/O beyond HLD 002's existing passthrough credential lookup.
- A restart resets all overrides to OFF; the matched-imposter path incurs no switch-related cost at all.

## Applies To

Goals 1 and 3 (the toggle and its fail-safe behaviour); the in-memory switch service and the passthrough
branch. Complements [LADR-001](../ladrs/LADR-001-in-memory-runtime-override-switch.md).
