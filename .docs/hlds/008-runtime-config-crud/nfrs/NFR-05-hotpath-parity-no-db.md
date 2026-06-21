# NFR-05: Imposter Hot-Path Parity & No-Database Operation (Compatibility)

**Status:** Accepted

## Requirement

The imposter hot-path contract from HLD 001 is unchanged by this design, and the **entire** management
surface — provider-config CRUD, credential CRUD/activation, and the authorization-override — functions with
**no database** (`ConnectionStrings:ImposterDb` absent). Matched imposter routes continue to use
registry-defined keys and never consult a credential store or override switch.

## Verification

- Integration test (L2): with no connection string configured, exercise full provider-config CRUD,
  credential create/update/activate/delete, and override arm/clear — all succeed in-memory.
- Regression test: existing HLD 001 imposter routing tests pass unchanged (model rewrite, caching injection,
  default passthrough), and a matched imposter route makes no credential-store / override-switch call.

## Acceptance Criteria

- A matched imposter route's outbound request is byte-for-byte equivalent to HLD 001 behaviour.
- No admin operation throws or silently no-ops due to a missing database (the prior no-op store is removed).
- The optional database backend, when configured, behaves exactly as HLD 002 for persisted credentials.

## Applies To

Goals 5, 6; the resolution path and the credential store abstraction
([LADR-05](../ladrs/LADR-05-settings-backed-provider-keyed-credentials.md),
[LADR-06](../ladrs/LADR-06-provider-addressable-override.md)). Upholds HLD 001 imposter guarantees and HLD 002
LADR-004 (overrides passthrough-only).
