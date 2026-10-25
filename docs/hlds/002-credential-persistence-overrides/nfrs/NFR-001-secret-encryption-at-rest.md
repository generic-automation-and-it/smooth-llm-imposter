# NFR-001 — Secret Encryption at Rest

- **Category:** Security
- **Status:** Accepted · 2026-06-15

Persisted provider secrets are **encrypted at rest** and never stored, logged, or returned in plaintext.

- Secrets are encrypted with `IDataProtector` (a dedicated, purpose-scoped protector) **before** they reach
  EF Core; the database column `SecretCiphertext` only ever holds ciphertext.
- The plaintext secret exists in memory only transiently: at create/rotate time (to encrypt) and at
  forward time (to decrypt and apply to the outbound request). It is never written to logs — logs carry
  credential `Name` + dialect only, consistent with [HLD 001 NFR-002](../../001-llm-imposter-routing/nfrs/NFR-002-credential-security.md).
- The admin API responses **never** include `SecretCiphertext` or any decrypted secret. Create/update accept
  a secret; reads return metadata only.
- The Data Protection key ring must be persisted and protected in production (e.g. keys directory with
  restricted ACLs, or a key-vault provider) so encrypted credentials survive restarts and host changes.

> **Amends [HLD 001 NFR-002](../../001-llm-imposter-routing/nfrs/NFR-002-credential-security.md).** HLD 001's
> "keys are never persisted" holds for the **imposter** path (config/env only). For the **passthrough** path,
> credentials may now be persisted — but only as ciphertext, never logged, never echoed.
