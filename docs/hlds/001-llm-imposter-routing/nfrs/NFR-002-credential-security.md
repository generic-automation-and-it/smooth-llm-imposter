# NFR-002 — Credential Security

- **Category:** Security
- **Status:** Accepted · 2026-06-14 · **Amended (2026-06-15)** by
  [HLD 002 NFR-001](../../002-credential-persistence-overrides/nfrs/NFR-001-secret-encryption-at-rest.md)

> **Amendment.** "Never persisted" holds for the **imposter** path (keys are config/env only). For the
> **passthrough** path, HLD 002 allows credentials to be persisted — but only as `IDataProtector` ciphertext,
> never logged, and never returned by the admin API. The "never written to logs / inbound auth never
> forwarded" guarantees below are unchanged.

Provider API keys are sourced from configuration/environment only. They are **never persisted**
and **never written to logs** (logs carry provider name + model names only). The inbound caller's
own `Authorization` / `x-api-key` is **never forwarded** upstream — the provider's configured key
replaces it.
