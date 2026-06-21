# NFR-002: Security / Authorization — toggle endpoints are admin-only

**Status:** Accepted

## Requirement

The override toggle endpoints (`PUT`/`DELETE`/`GET /routing/{dialect}/override-authorization`) change how the
router authenticates to upstreams and therefore must be **authenticated and authorized** with the existing
admin authorization policy — they must **not** be reachable by the unauthenticated routing surface. Anonymous
access returns `401`; authenticated-but-unauthorized returns `403`. This is the same boundary HLD 002 applies
to `/admin/credentials*` ([HLD 002 NFR-002](../../002-credential-persistence-overrides/nfrs/NFR-002-admin-endpoint-authorization.md)).

## Verification

- Integration test: `PUT`/`DELETE`/`GET` without an `X-Admin-Api-Key` header → `401`.
- Integration test: with a valid admin key → the toggle succeeds (or returns the `403` of [LADR-005](../ladrs/LADR-005-403-no-active-credential-fail-closed.md) when no active credential).
- Review: the toggle route group opts into `AdminPolicy`; the proxy dialect endpoints (`/v1/*`) remain key-less and unauthenticated.

## Acceptance Criteria

- The switch cannot be flipped or read without admin authorization.
- The proxy hot-path endpoints stay unauthenticated (HLD 001) — only the new control endpoints are gated.
- A toggle attempt's outcome distinguishes "not authorized" (`401`/`403` for caller auth) from "nothing to arm" (`403`, no active credential).

## Applies To

Goal 1 (the toggle endpoints); the admin control surface. Distinct from NFR-001, which governs the wire
behaviour of the passthrough forward.
