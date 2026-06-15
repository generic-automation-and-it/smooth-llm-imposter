# HLD 002 — Credential Persistence & Overrides

Status: Proposed · 2026-06-15

> **Supersedes / amends HLD 001.** This design reintroduces persistence that HLD 001 deliberately
> removed. It **supersedes [HLD 001 LADR-002](../001-llm-imposter-routing/ladrs/LADR-002-stateless-no-ef-postgresql.md)**
> (stateless, no EF/PostgreSQL) and **amends [HLD 001 NFR-002](../001-llm-imposter-routing/nfrs/NFR-002-credential-security.md)**
> (credentials never persisted). The routing **hot path is unchanged** — see [LADR-004](ladrs/LADR-004-overrides-passthrough-only.md).

## Problem

HLD 001 sources every provider key from configuration/environment and stores nothing. That is the right
default for impostering, but it forces an operator to redeploy (or re-login the SDK) to switch which key a
**passthrough** call uses — e.g. flipping a Claude Code session between a *work* and a *private* account, or
pointing OpenAI passthrough at a different organisation key. Operators also hit dialect mismatches: a caller
that only knows how to send an `x-api-key` may need the upstream to receive a `Bearer` token, or vice-versa.

We want, **for the passthrough/default path only**: persisted, encrypted-at-rest credentials that can be
managed and switched at runtime through an admin API, separated by provider dialect, without ever touching
the imposter hot path that HLD 001 specifies.

## Solution overview

Reintroduce EF Core + PostgreSQL for **one bounded concern**: stored passthrough credentials. A single
`ProviderCredential` table (TPH, named discriminator `ProviderDialect`) holds OpenAI and Anthropic
credentials behind a shared base. Secrets are encrypted with `IDataProtector` before persistence — only
ciphertext is stored, and secrets are never logged or returned by the API.

Routing is extended at exactly one seam: when a request **does not match** any imposter mapping and falls to
the dialect's default/passthrough provider (HLD 001 [LADR-005](../001-llm-imposter-routing/ladrs/LADR-005-no-default-passthrough-type-only.md)),
the router consults the persisted **active** credential for that dialect. If one exists it supplies the
outbound secret and (optionally) translates the auth scheme; otherwise behaviour is exactly as HLD 001.
**Matched imposter routes keep using settings-defined keys and are not affected.**

Credential management uses the project-standard **Mediator + FluentValidation** admin endpoints (not the raw
routing hot path — see [LADR-005](ladrs/LADR-005-admin-crud-mediator-while-hotpath-raw.md)).

## Diagrams

- [System context](diagrams/system-context.md) — actors, upstreams, and the new PostgreSQL store.
- [Credential resolution](diagrams/credential-resolution.md) — where stored credentials enter the passthrough path.
- [Credential ER](diagrams/credential-er.md) — the TPH credential model.

## Data model

TPH single table `ProviderCredentials`, discriminator column **`ProviderDialect`** (`openai` | `anthropic`).

| Field | Type | Notes |
|---|---|---|
| `Id` | `Guid` | PK (`BaseEntity`) |
| `CreatedAtUtc` / `UpdatedAtUtc` | `DateTime` (UTC) | audit (`BaseEntity`) |
| `Name` | `string` | operator label, unique per dialect (e.g. `work`, `private`) |
| `ProviderDialect` | discriminator | `openai` → `OpenAiCredential`, `anthropic` → `AnthropicCredential` |
| `SecretCiphertext` | `string` | `IDataProtector`-encrypted; **never** plaintext, never logged |
| `AuthScheme` | enum | `ApiKey` \| `Bearer` — outbound scheme applied on passthrough |
| `IsActive` | `bool` | at most one active credential per dialect (enforced on activate) |
| `BaseUrlOverride` | `string?` | optional passthrough upstream root (no `/v1`), else dialect default |

Subtypes (`OpenAiCredential`, `AnthropicCredential`) carry only dialect-specific extras (e.g. Anthropic
`anthropic-version`). Most fields live on the abstract root.

## Admin API

All admin endpoints are authenticated/authorised (see [NFR-002](nfrs/NFR-002-admin-endpoint-authorization.md)),
route through Mediator + FluentValidation, and **never echo `SecretCiphertext` or any plaintext secret**.

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/admin/credentials` | create a credential for a dialect (secret encrypted on write) |
| `GET` | `/admin/credentials` | list (metadata only — no secrets) |
| `GET` | `/admin/credentials/{id}` | fetch one (metadata only) |
| `PUT` | `/admin/credentials/{id}` | update label / scheme / base-url / rotate secret |
| `DELETE` | `/admin/credentials/{id}` | remove |
| `PUT` | `/admin/credentials/{id}/activate` | make this the active credential for its dialect (deactivates siblings) |

## Architecture

Clean Architecture, persistence reintroduced for this concern only:

- **Domain** — `BaseEntity`, `ProviderCredential` (abstract) + `OpenAiCredential` / `AnthropicCredential`.
- **Application** — `Features/Credentials/` admin slices (Mediator handlers + FluentValidation validators),
  an `ICredentialStore` abstraction, and a passthrough-resolution extension consumed by the router.
- **Infrastructure** — EF Core `DbContext`, TPH `IEntityTypeConfiguration`, design-time factory, migrations
  (`[ExcludeFromCodeCoverage]`), `IDataProtector`-backed secret protector, credential repository/store.
- **Host** — minimal-API admin endpoint group + options binding; routing endpoints unchanged.

Routing body transformation stays string-in/string-out (HLD 001); only the passthrough **credential lookup**
is added, behind `ICredentialStore`.

## Non-functional requirements

- [NFR-001 — Secret encryption at rest](nfrs/NFR-001-secret-encryption-at-rest.md)
- [NFR-002 — Admin endpoint authorization](nfrs/NFR-002-admin-endpoint-authorization.md)

## Architecture decisions (LADRs)

- [LADR-001 — Reintroduce EF Core + PostgreSQL for credentials](ladrs/LADR-001-ef-postgresql-for-credentials.md)
- [LADR-002 — TPH with a named discriminator over a shared base](ladrs/LADR-002-tph-named-discriminator.md)
- [LADR-003 — Encrypt secrets with `IDataProtector`](ladrs/LADR-003-idataprotector-secret-encryption.md)
- [LADR-004 — Overrides scoped to the passthrough path only](ladrs/LADR-004-overrides-passthrough-only.md)
- [LADR-005 — Admin CRUD via Mediator/FluentValidation; hot path stays raw](ladrs/LADR-005-admin-crud-mediator-while-hotpath-raw.md)

## Out of scope (for now)

Per-request key selection by caller header, multi-tenant credential isolation, key-vault/HSM backends
(the `IDataProtector` seam leaves room), usage metering, and applying stored credentials to **matched
imposter** routes (intentionally excluded — imposter keys remain config-only per HLD 001).
