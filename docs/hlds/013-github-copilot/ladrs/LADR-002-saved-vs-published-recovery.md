# LADR-002: Accept the saved-but-unpublished window with bounded retry and restart recovery

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-20 |
| **Deciders** | SmoothLlmImposter maintainers |

## Context

Completing a login writes a SQLite row and publishes an immutable in-memory revision plus two provider
definitions. These are separate stores; wrapping them in one transaction is not possible without persisting the
runtime registry too, which HLD 008 deliberately keeps ephemeral. A crash or registry-validation failure between
commit and publication would otherwise strand a saved credential with no routable pair.

## Decision

SQLite commit precedes publication, and a saved-but-unpublished window is explicitly accepted:

- Complete retries publication a small bounded number of times from the **already-saved revision**, using the
  same per-prefix gate, and returns 200 only after the pair is published.
- Retry exhaustion returns 503 with `savedRevision`, `publicationReady:false` and a restart hint; the row stays
  intact. A later Complete retry republishes only and never re-exchanges the consumed device grant.
- Startup seeds configured providers, hydrates durable connections and re-materializes missing pairs before
  serving requests.
- Admin GETs report `savedRevision`, `publishedRevision` and readiness without writing or reconciling.
- No reconcile endpoint, indefinite publication worker, queue or compensating credential deletion is added.

## Consequences

- A first login's pair is not routable during the window; on re-login a previously published still-valid
  revision may keep serving until replacement publication.
- Publication failures are visible through the admin status and recoverable without another GitHub login.
- The registry mutation is one validated batch, so unrelated admin edits are preserved and a half-published
  pair cannot be observed.
