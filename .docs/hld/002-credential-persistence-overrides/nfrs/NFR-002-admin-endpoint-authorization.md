# NFR-002 — Admin Endpoint Authorization

- **Category:** Security
- **Status:** Proposed · 2026-06-15

The credential admin API (`/admin/credentials*`) manages secrets and therefore must be **authenticated and
authorized** — it must not be reachable by the same unauthenticated surface as the routing endpoints.

- All `/admin/credentials*` endpoints require an authenticated operator with an admin authorization policy;
  anonymous access returns `401`, authenticated-but-unauthorized returns `403`.
- The admin surface is **separate from the routing dialect endpoints**, which remain key-less and
  unauthenticated per HLD 001 (callers are authenticated upstream by the provider key, not by this router).
- Credential **mutations** (create / update / delete / activate) should be auditable — at minimum an
  `Information` log entry recording actor, action, credential `Name` + dialect (never the secret).
- The auth mechanism (API key header, bearer/JWT, mTLS, or network-level restriction) is an implementation
  choice for the executing task; the requirement is that the admin surface is not publicly writable.

> Rationale: HLD 001 could leave all endpoints unauthenticated because the service stored nothing sensitive.
> Once secrets are persisted and mutable over HTTP, the management surface becomes a privileged boundary.
