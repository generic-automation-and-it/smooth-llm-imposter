# Credential admin & authorization override

## TL;DR

The credential admin API manages **passthrough/default** credentials at runtime:

- **Settings-backed passthrough credentials** (HLD 008) — `/admin/credentials` stores credentials keyed by
  provider dictionary key; with no database they live in memory until restart.
- **Optional PostgreSQL persistence** — when `ConnectionStrings:ImposterDb` is set, the same API uses the
  encrypted EF backend.
- **Authorization override** (HLD 003 amended by HLD 008) — a provider-addressable toggle that forces the
  active stored credential to be
  sent as `Authorization: Bearer` on that passthrough path.

No database is required for CRUD/activation/override. Without a connection string, writes succeed in memory
and are forgotten on restart.

## When you do — and don't — need this

| You want to… | Use this? |
|---|---|
| Route a specific model to an imposter upstream (incl. the `*-personal` providers) | **No.** Imposter routes authenticate **only** with the provider's `Secret` (config/env, e.g. `ANTHROPIC_PERSONAL_AUTHORIZATION_BEARER`). They never read this store. |
| Give the catch-all `*-default` passthrough a stored credential instead of forwarding the caller's own auth | **Yes.** |
| Force the passthrough to present a stored Bearer token for a provider (HLD 003 / HLD 008) | **Yes.** |

> **Key boundary.** Matched imposter routes are config-key-only and DB-free (HLD 002 LADR-004). This whole
> document is about the **passthrough / default** path. If you only run imposter routes, you never need a
> database. See [`setup.md` → Configure routing](../setup.md#configure-routing) and HLD 007 LADR-04.

## Optional PostgreSQL persistence

The in-memory backend is the default. To persist credentials across restart, run PostgreSQL and set the
connection string (env wins over `appsettings.json`):

```bash
export ConnectionStrings__ImposterDb="Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres"
export Admin__ApiKey="choose-a-strong-admin-key"
```

Apply the EF Core migrations to that database:

```bash
dotnet ef database update --project src/SmoothLlmImposter.Infrastructure
```

Restart the Host. `AddInfrastructure` now wires EF Core + the PostgreSQL-backed `CredentialStore`.

### What "unset" actually means

When `ConnectionStrings:ImposterDb` is **blank or absent**, `AddInfrastructure` registers
`InMemoryCredentialStore`. There is **no database connection**, but `/admin/credentials` mutations succeed
and the authorization override can be armed after an active credential exists. State is process-local and
lost on restart.

## Admin authentication

The `/admin/credentials` and `/routing/{dialect}/{provider}/override-authorization` groups are guarded by the
`X-Admin-Api-Key` header (constant-time matched), unlike the key-less proxy endpoints:

- `Admin:ApiKey` — full admin (`CredentialAdmin` role; required for all mutations).
- `Admin:OperatorApiKey` — operator (authenticated, non-admin).

## Endpoints

### Stored credentials — `/admin/credentials`

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/admin/credentials` | Create a credential (`{ ProviderDialect, ProviderName?, Name, Secret, AuthScheme, BaseUrlOverride?, AnthropicVersion? }`). `ProviderName` is the provider dictionary key; omit it to target the dialect default. `Name` is a free-form label. |
| `GET` | `/admin/credentials` / `/{id}` | List / fetch. |
| `PUT` | `/admin/credentials/{id}` | Update metadata + rotate `Secret`; optional `ProviderName` retargets the credential and deactivates it. |
| `PUT` | `/admin/credentials/{id}/activate` | Mark this the **active** credential for its provider. |
| `DELETE` | `/admin/credentials/{id}` | Remove. |

Credentials bind by `(ProviderDialect, ProviderName)` + `IsActive`; `ProviderName` is the stable provider
dictionary key such as `anthropic-default`. `Name` is cosmetic; name it for *you*.

```bash
# Create, then activate, an Anthropic passthrough credential
curl -X POST http://localhost:5080/admin/credentials \
  -H "X-Admin-Api-Key: $Admin__ApiKey" -H "content-type: application/json" \
  -d '{ "ProviderDialect": "anthropic", "ProviderName": "anthropic-default", "Name": "my-anthropic-passthrough", "Secret": "sk-...", "AuthScheme": "Bearer" }'

curl -X PUT http://localhost:5080/admin/credentials/<guid>/activate -H "X-Admin-Api-Key: $Admin__ApiKey"
```

### Authorization override — `/routing/{dialect}/{provider}/override-authorization`

| Method | Path | Purpose |
|--------|------|---------|
| `PUT` | `/routing/{dialect}/{provider}/override-authorization` | Arm the force-Bearer override (no body). **403** if the provider has no active stored credential. |
| `PUT` | `/routing/{dialect}/override-authorization` | Dialect-only fallback: targets the dialect's enabled default provider. |
| `DELETE` | same | Disarm. |
| `GET` | same | Report `{ Dialect, ProviderName, Enabled }`. |

```bash
curl -X PUT http://localhost:5080/routing/anthropic/anthropic-default/override-authorization -H "X-Admin-Api-Key: $Admin__ApiKey"
```

When armed, the passthrough presents the active credential's secret as `Authorization: Bearer` (omitting
`x-api-key`), regardless of its stored `AuthScheme`. It only ever affects the passthrough path — matched
imposter routes never read the switch.

## Data Protection keys

With the EF backend, stored secrets are encrypted at rest via ASP.NET Core Data Protection. In a container,
persist the keyring so
encrypted secrets survive a rebuild — e.g. `-v slli-dpkeys:/home/app/.aspnet/DataProtection-Keys` (Docker) or
the `slli-dpkeys` volume in `docker-compose.yml` (Compose). Without persisted keys, a restart can't decrypt
previously stored credentials.

## Related

- [`setup.md`](../setup.md) — base setup; routing config and the imposter-`Secret`/env path (the common case).
- HLD 002 (credential persistence & overrides) and HLD 003 (authorization override) under `.docs/hld/`.
- `Features/AuthorizationOverride/AUTHORIZATION_OVERRIDE_AGENTS.md` — the toggle slices and endpoint contract.
