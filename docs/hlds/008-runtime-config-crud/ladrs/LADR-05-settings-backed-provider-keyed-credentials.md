# LADR-05: Credentials Settings-Backed and Provider-Keyed; Database Optional

**Status:** Accepted

## Context

HLD 002 stores passthrough credentials in PostgreSQL via EF Core, keyed by **dialect** (one active
credential per `openai`/`anthropic`). Two problems surface against issue #48: credential management **only
works when a database connection string is present** (without it, writes silently fail through a no-op
store), and a dialect can now have **several named providers** (HLD 007), so "the active credential for a
dialect" is no longer expressive enough. Credentials must be manageable on the fly, with no database, and
scoped to the specific provider they serve.

## Decision

**Re-base** credentials on the runtime settings model and **key them by the provider dictionary key** rather than dialect.
The default backend is an **in-memory settings-backed store** whose writes succeed without any database. The
existing encrypted PostgreSQL/EF Core backend is **retained as an opt-in** behind the same store
abstraction: when a connection string is configured it is used (and HLD 002
[LADR-003](../../002-credential-persistence-overrides/ladrs/LADR-003-idataprotector-secret-encryption.md)
encryption-at-rest still applies); otherwise the in-memory store is the default. This **supersedes** HLD 002
[LADR-001](../../002-credential-persistence-overrides/ladrs/LADR-001-ef-postgresql-for-credentials.md)
(database is now optional, not mandatory) and
[LADR-002](../../002-credential-persistence-overrides/ladrs/LADR-002-tph-named-discriminator.md)
(provider-keyed, not dialect-discriminated).

## Alternatives Considered

- **Drop the database entirely** — rejected by product decision: keep it as an option for operators who want
  encrypted persistence.
- **Keep dialect keying, add provider as a tag** — rejected: resolution and activation need an unambiguous
  per-provider key, not a dialect with a secondary filter.
- **Fold the secret into the provider-config registry** — rejected: violates the secret/​config boundary
  ([LADR-02](./LADR-02-config-secret-boundaries.md)); credentials stay a distinct collection.

## Consequences

- Full credential CRUD + activation works with no database — the no-op silent-fail store is removed.
- Passthrough credential lookup keys by `(dialect, provider key)` instead of dialect alone; the resolver must
  know the resolved provider's stable dictionary key.
- Each provider can hold its own active credential; activation deactivates siblings only within the same
  `(dialect, provider key)` key. Dialect-only admin calls resolve their provider key through the dialect's
  enabled default provider.
- Two backends behind one abstraction: in-memory (default, ephemeral) and EF/PostgreSQL (opt-in, encrypted,
  persistent). Their differing durability must be documented.
- HLD 002's data model and its dialect-keyed uniqueness rule are superseded; HLD 002 files get supersession
  notes.

## Related

- **LADR-02** — the secret boundary this collection owns.
- **LADR-06** — provider-addressable activation/override that consumes these credentials.
- Supersedes HLD 002 LADR-001 and LADR-002; preserves HLD 002 LADR-003 for the opt-in backend.
