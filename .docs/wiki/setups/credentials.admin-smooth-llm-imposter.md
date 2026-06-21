# Credential persistence & authorization override (optional, needs PostgreSQL)

## TL;DR

Two **optional** add-ons share one switch — a PostgreSQL connection string:

- **Stored passthrough credentials** (HLD 002) — the `/admin/credentials` API stores encrypted credentials
  the router presents on the **passthrough / default** path (for models that don't match an imposter route).
- **Authorization override** (HLD 003) — a per-dialect toggle that forces the active stored credential to be
  sent as `Authorization: Bearer` on that passthrough path.

Both are **off by default**. The router is stateless and key-less unless you set
`ConnectionStrings:ImposterDb`; until you do, it boots with **no database** and these features are inert.

## When you do — and don't — need this

| You want to… | Use this? |
|---|---|
| Route a specific model to an imposter upstream (incl. the `*-personal` providers) | **No.** Imposter routes authenticate **only** with the provider's `Secret` (config/env, e.g. `ANTHROPIC_PERSONAL_AUTHORIZATION_BEARER`). They never read this store. |
| Give the catch-all `*-default` passthrough a stored credential instead of forwarding the caller's own auth | **Yes.** |
| Force the passthrough to present a stored Bearer token for a dialect (HLD 003) | **Yes.** |

> **Key boundary.** Matched imposter routes are config-key-only and DB-free (HLD 002 LADR-004). This whole
> document is about the **passthrough / default** path. If you only run imposter routes, you never need a
> database. See [`setup.md` → Configure routing](../setup.md#configure-routing) and HLD 007 LADR-04.

## Enabling it

1. Run PostgreSQL and set the connection string (env wins over `appsettings.json`):

   ```bash
   export ConnectionStrings__ImposterDb="Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres"
   export Admin__ApiKey="choose-a-strong-admin-key"
   ```

2. Apply the EF Core migrations to that database (`dotnet ef database update --project src/SmoothLlmImposter.Infrastructure`).
3. Restart the Host. `AddInfrastructure` now wires EF Core + the PostgreSQL-backed `CredentialStore`.

### What "unset" actually means

When `ConnectionStrings:ImposterDb` is **blank or absent**, `AddInfrastructure` registers a
`NullCredentialStore` — there is **no database connection and no runtime default**. On that store:

- the passthrough seam resolves a `null` credential and forwards the **caller's own** `Authorization` /
  `x-api-key` (the stateless/key-less default), and
- every `/admin/credentials` mutation fails with *"Credential persistence is not configured. Set
  ConnectionStrings:ImposterDb…"*, and arming the override returns **403** (no active credential).

> The string `Host=localhost;Port=5432;Database=smoothllmimposter;Username=postgres;Password=postgres` is **only**
> the design-time default baked into `ImposterDbContextFactory` for `dotnet ef` migrations. It is **not** a
> runtime fallback — an unset connection string does not silently connect to localhost.

## Admin authentication

The `/admin/credentials` and `/routing/{dialect}/override-authorization` groups are guarded by the
`X-Admin-Api-Key` header (constant-time matched), unlike the key-less proxy endpoints:

- `Admin:ApiKey` — full admin (`CredentialAdmin` role; required for all mutations).
- `Admin:OperatorApiKey` — operator (authenticated, non-admin).

## Endpoints

### Stored credentials — `/admin/credentials`

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/admin/credentials` | Create a credential (`{ ProviderDialect, Name, Secret, AuthScheme, BaseUrlOverride?, AnthropicVersion? }`). `Name` is a free-form label, not a provider key. |
| `GET` | `/admin/credentials` / `/{id}` | List / fetch. |
| `PUT` | `/admin/credentials/{id}` | Update metadata + rotate `Secret` (`{ Name, AuthScheme, Secret?, BaseUrlOverride?, AnthropicVersion? }`). |
| `PUT` | `/admin/credentials/{id}/activate` | Mark this the **active** credential for its dialect (the one the passthrough presents). |
| `DELETE` | `/admin/credentials/{id}` | Remove. |

Credentials bind **by dialect** (`openai` / `anthropic`) + `IsActive` — `GetActiveAsync(dialect)` selects the
one the passthrough uses. `Name` is cosmetic; name it for *you* (e.g. `my-anthropic-passthrough`), not after a
provider key.

```bash
# Create, then activate, an Anthropic passthrough credential
curl -X POST http://localhost:5080/admin/credentials \
  -H "X-Admin-Api-Key: $Admin__ApiKey" -H "content-type: application/json" \
  -d '{ "ProviderDialect": "anthropic", "Name": "my-anthropic-passthrough", "Secret": "sk-...", "AuthScheme": "Bearer" }'

curl -X PUT http://localhost:5080/admin/credentials/<guid>/activate -H "X-Admin-Api-Key: $Admin__ApiKey"
```

### Authorization override — `/routing/{dialect}/override-authorization`

| Method | Path | Purpose |
|--------|------|---------|
| `PUT` | `/routing/{dialect}/override-authorization` | Arm the force-Bearer override (no body). **403** if the dialect has no active stored credential. |
| `DELETE` | same | Disarm. |
| `GET` | same | Report `{ Dialect, Enabled }`. |

```bash
curl -X PUT http://localhost:5080/routing/anthropic/override-authorization -H "X-Admin-Api-Key: $Admin__ApiKey"
```

When armed, the passthrough presents the active credential's secret as `Authorization: Bearer` (omitting
`x-api-key`), regardless of its stored `AuthScheme`. It only ever affects the passthrough path — matched
imposter routes never read the switch.

## Data Protection keys

Stored secrets are encrypted at rest via ASP.NET Core Data Protection. In a container, persist the keyring so
encrypted secrets survive a rebuild — e.g. `-v slli-dpkeys:/home/app/.aspnet/DataProtection-Keys` (Docker) or
the `slli-dpkeys` volume in `docker-compose.yml` (Compose). Without persisted keys, a restart can't decrypt
previously stored credentials.

## Related

- [`setup.md`](../setup.md) — base setup; routing config and the imposter-`Secret`/env path (the common case).
- HLD 002 (credential persistence & overrides) and HLD 003 (authorization override) under `.docs/hld/`.
- `Features/AuthorizationOverride/AUTHORIZATION_OVERRIDE_AGENTS.md` — the toggle slices and endpoint contract.
