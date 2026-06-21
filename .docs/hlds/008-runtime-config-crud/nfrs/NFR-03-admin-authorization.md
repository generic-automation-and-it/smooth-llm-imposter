# NFR-03: Admin Endpoint Authorization (Security)

**Status:** Draft

## Requirement

Every provider-config, credential, and authorization-override endpoint requires the existing admin
authorization (`X-Admin-Api-Key` matching the configured admin key, `CredentialAdmin` policy). A missing key
yields `401`; a non-admin (e.g. operator) key yields `403`. No mutating endpoint is reachable
unauthenticated.

## Verification

Integration test (L2): for each endpoint, assert `401` with no key, `403` with a non-admin key, and success
with the admin key — mirroring the existing credential-admin authorization tests.

## Acceptance Criteria

- Provider-config `GET`/`PUT`/`DELETE`/enable/disable all enforce the admin policy.
- Credential and override endpoints enforce the same policy under provider-addressable routes.
- The data-plane proxy endpoints (`/openai/...`, `/anthropic/...`) remain unaffected by this policy (they are
  not admin endpoints).

## Applies To

Goals 1, 4, 5, 6; all `/admin/*` and `/routing/*` control endpoints. Reuses the existing
`AdminApiKeyAuthenticationHandler` admin policy.
