# LADR-003 — Encrypt secrets with `IDataProtector`

- **Date / Status:** 2026-06-15 · Proposed

## Context

Persisted provider secrets must not sit in the database as plaintext ([NFR-001](../nfrs/NFR-001-secret-encryption-at-rest.md)).
The service is a self-contained ASP.NET Core app; we want application-layer encryption without mandating an
external secrets backend for v1.

## Decision

Encrypt secrets with ASP.NET Core **Data Protection** (`IDataProtector`) using a dedicated, purpose-scoped
protector (e.g. `CreateProtector("ProviderCredential.Secret")`). Encrypt on write (create/rotate), store only
`SecretCiphertext`, decrypt just-in-time at forward. Expose the protect/unprotect behind a small
`ISecretProtector` abstraction so the backend can later be swapped for a key vault / HSM without touching
domain or store code.

Alternatives rejected: storing plaintext (violates NFR-001); column-level DB encryption only (key management
leaks to the DB operator, and the secret is still plaintext in transit to the app); rolling a bespoke
AES wrapper (re-implements key-ring rotation that Data Protection already provides).

## Consequences

- Requires Data Protection to be configured with a **persisted, protected key ring** in production (keys
  directory with restricted ACLs or a configured key-vault provider). An ephemeral key ring would make stored
  ciphertext undecryptable after restart — a deployment must not rely on the default in-memory keys.
- Rotating the underlying Data Protection keys re-wraps transparently; rotating a *provider* secret is a
  normal update that re-encrypts and overwrites `SecretCiphertext`.
- The `ISecretProtector` seam is the future extension point for vault/HSM-backed protection (see HLD 002
  README "Out of scope").
