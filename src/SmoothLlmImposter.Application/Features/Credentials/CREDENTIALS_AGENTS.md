# Credentials — Feature Context

## TL;DR

Passthrough credentials are for passthrough/default routing only. They are stored as protected ciphertext,
managed through `/admin/credentials`, keyed by `(ProviderDialect, ProviderName)`, and resolved only after
routing determines `IsImposter == false`.

## Non-Negotiables

- Never return or log plaintext secrets or `SecretCiphertext`.
- Matched imposter routes stay config-key-only and must not read the credential store.
- Exactly one active credential per `(dialect, providerName)` is enforced by activation in the store transaction.
- No database is required for credential CRUD/activation: `InMemoryCredentialStore` is the default backend;
  EF/PostgreSQL is opt-in when `ConnectionStrings:ImposterDb` is configured.
- Admin CRUD uses Mediator application slices with the FluentValidation pipeline; routing remains raw.
- Validation failures surface as HTTP 400 via the Host's global `ValidationExceptionHandler` (`IExceptionHandler`).
  Endpoints must NOT re-catch `ValidationException` per-handler — the central handler owns the `problem+json` 400.
- Admin API keys are compared with `CryptographicOperations.FixedTimeEquals` (constant-time); never use `==`.
- `ProviderDialect` discriminator values are stable persisted tokens: `openai` and `anthropic`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-15 | Created for HLD 002 credential persistence and passthrough overrides. | HLD 002 |
| 2026-06-15 | Centralized validation→400 via global `IExceptionHandler`; constant-time admin key comparison. | HLD 002 |
| 2026-06-21 | HLD 008 Phase 2: credentials are provider-keyed, in-memory by default, EF optional; activation is scoped per provider. | #50 |
