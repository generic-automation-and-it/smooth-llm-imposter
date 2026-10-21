# LADR-003: Credential removal is volume-level only in v1

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-20 |
| **Deciders** | SmoothLlmImposter maintainers |

## Context

The feature stores credentials in a dedicated local Docker volume with an isolated Data Protection key ring.
An in-app delete/logout endpoint would need to define cross-instance coordination, GitHub-side revocation and
forensic-erasure expectations that this single-instance, local-volume scope cannot honor.

## Decision

V1 has **no** connection delete, logout, revoke or revocation API, and no generic-CRUD bypass.

- Disabling the feature does not delete data; re-enabling reopens it.
- Pulling/recreating the container against the same named volume retains decryptable credentials.
- Full deletion means stopping/removing the service **and** removing the exact dedicated volume — affecting
  every saved prefix in that installation. `docker compose down -v` is not selective feature cleanup.
- Expiry and upstream-401 invalidation retain the row and only mark routing unusable. GitHub-side OAuth
  revocation remains a separate manual operator action; local removal is logical deletion, not secure erase.

## Consequences

- A stolen whole-volume image is outside the protection boundary; the documentation states this plainly rather
  than calling the feature a vault.
- Operators need an explicit, documented volume name to clean up; the compose overlay and Docker run recipe
  use `smooth-llm-imposter-copilot`.
