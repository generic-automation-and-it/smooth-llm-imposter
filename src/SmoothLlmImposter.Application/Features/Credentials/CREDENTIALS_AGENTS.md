# Credentials — Feature Context

## TL;DR

Persisted credentials are for passthrough/default routing only. They are stored as encrypted ciphertext,
managed through `/admin/credentials`, and resolved only after routing determines `IsImposter == false`.

## Non-Negotiables

- Never return or log plaintext secrets or `SecretCiphertext`.
- Matched imposter routes stay config-key-only and must not read the credential store.
- Exactly one active credential per dialect is enforced by activation in the store transaction.
- Admin CRUD uses Mediator application slices with the FluentValidation pipeline; routing remains raw.
- `ProviderDialect` discriminator values are stable persisted tokens: `openai` and `anthropic`.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-06-15 | Created for HLD 002 credential persistence and passthrough overrides. | HLD 002 |
