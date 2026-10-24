# Credentials — Feature Context

## TL;DR

Passthrough/default-routing credentials only: protected ciphertext managed via `/admin/credentials`, keyed by `(ProviderDialect, ProviderName)`, resolved after routing determines `IsImposter == false`.

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

## HLD 013 exception — managed GitHub Copilot credentials

Matched imposter routes are still config-only **except** for reserved Copilot provider keys: those resolve a
managed Bearer token and origin from the feature's published connection snapshot (see
[`Features/GithubCopilot/AGENTS.md`](../GithubCopilot/AGENTS.md)). The generic
`ICredentialStore` and the passthrough override path are unchanged, and the Copilot store is a separate
feature-owned SQLite file with its own key ring — never add Copilot tokens to this credential subsystem.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-15 | Created for HLD 002 credential persistence and passthrough overrides. | HLD 002 |
| 2026-06-15 | Centralized validation→400 via global `IExceptionHandler`; constant-time admin key comparison. | HLD 002 |
| 2026-06-21 | HLD 008 Phase 2: credentials are provider-keyed, in-memory by default, EF optional; activation is scoped per provider. | #50 |
| 2026-10-20 | Documented the narrow HLD 013 exception and its boundary from the generic credential store. | #126 |
| 2026-10-24 | Renamed to nested `AGENTS.md` (Claude Code auto-discovery); in-file links updated. No behavior change. | #152 |
