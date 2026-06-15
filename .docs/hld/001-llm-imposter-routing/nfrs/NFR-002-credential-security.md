# NFR-002 — Credential Security

- **Category:** Security
- **Status:** Accepted · 2026-06-14

Provider API keys are sourced from configuration/environment only. They are **never persisted**
and **never written to logs** (logs carry provider name + model names only). The inbound caller's
own `Authorization` / `x-api-key` is **never forwarded** upstream — the provider's configured key
replaces it.
