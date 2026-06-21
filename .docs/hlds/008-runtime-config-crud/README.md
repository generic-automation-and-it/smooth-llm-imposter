# Runtime Config CRUD & Provider-Addressable Credentials — High-Level Design

| | |
|---|---|
| **Status** | In Prototype |
| **Owner** | @generic-automation-and-it/project |
| **Tracker** | [#48 — All configurations must be insert, update, get and deletable to change on the fly](https://github.com/generic-automation-and-it/smooth-devex-template/issues/48) |
| **Last updated** | 2026-06-21 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker.

> **Supersedes / amends earlier HLDs.** This design makes the named-provider configuration
> (HLD 007) **runtime-mutable** and re-bases the credential model (HLD 002) on settings rather
> than a mandatory database. It **supersedes** HLD 002
> [LADR-001](../002-credential-persistence-overrides/ladrs/LADR-001-ef-postgresql-for-credentials.md)
> (mandatory PostgreSQL → **optional** backend) and
> [LADR-002](../002-credential-persistence-overrides/ladrs/LADR-002-tph-named-discriminator.md)
> (dialect-keyed → **provider-keyed**). It **amends** HLD 003
> ([Passthrough Authorization Override](../003-passthrough-authorization-override/README.md)) to be
> provider-addressable, and HLD 007
> ([Named Provider Config](../007-named-provider-env-overrides/README.md)) by making the registry
> mutable at runtime. HLD 002
> [LADR-003](../002-credential-persistence-overrides/ladrs/LADR-003-idataprotector-secret-encryption.md)
> (encrypt secrets with `IDataProtector`) **still applies to the optional database backend only**. The
> **imposter hot-path contract from HLD 001 is unchanged.**

## Intent

Today the router's configuration is frozen at process start: the provider catalog is built once from
`IOptions` and never re-read, and stored credentials require a PostgreSQL database and are keyed by dialect
only. Operators cannot enable/disable a route, rewrite a model mapping, rotate a secret, or add/remove a
provider without a redeploy — and cannot manage credentials at all without a database. This design makes the
provider configuration and its credentials **insert/update/get/delete-able on the fly**, in-memory and
never persisted, while keeping the imposter hot path and its statelessness guarantees intact.

## Key Goals

### 1. Runtime CRUD over the provider routing dictionary

The `Imposter:Providers` dictionary becomes a **runtime-mutable registry**, addressed by its existing
dictionary key. Operators can list, fetch, upsert (`PUT` with a full body), and delete a provider's routing
configuration — dialect, base URL, auth scheme, model mappings, default flag, normalization — through an
authenticated admin API, without redeploying. The registry is **in-memory only and never persisted**; it is
seeded at startup from configuration + environment, and a restart returns it to the configured baseline.

**Acceptance criteria / DoD**

- A `GET` of a provider returns a body shaped so it can be edited and sent back as a `PUT` (round-trip),
  with the secret omitted (see Goal 4).
- A `PUT`/`DELETE`/insert against a provider key changes routing for subsequent requests with no restart.
- The registry holds no I/O dependency; with no database configured the full provider CRUD still works.

### 2. Live enable/disable of a route

A provider carries an **`Enabled`** flag. Disabling a provider removes it from routing **without deleting its
configuration** — it is excluded from both imposter model-matching and default/passthrough selection, as if
absent. Re-enabling restores it. This lets an operator park a route and bring it back without re-entering its
configuration.

**Acceptance criteria / DoD**

- Toggling `Enabled` via the admin API takes effect on the next inbound request.
- A disabled provider never matches a model mapping and is never chosen as a dialect default.
- A disabled provider's full configuration survives the toggle and is restored on re-enable.

### 3. Mutations take effect on the live routing path

Configuration is consumed through `IOptionsSnapshot` rather than a once-built singleton, so each inbound
request observes the current registry. A successful admin write is reflected on the **next** request — there
is no caching window an operator must wait out, and no redeploy.

**Acceptance criteria / DoD**

- After a successful mutation, the very next proxied request routes per the new configuration.
- The imposter hot-path behaviour and per-request validation contract from HLD 001 are unchanged.

### 4. The secret stays behind a separate credential boundary

Routing configuration and secrets are **two distinct concerns over the registry**. The provider-config API
never returns or accepts a secret; secrets are read/rotated only through the credential API. This preserves
the "secret never echoed" posture while still letting operators change everything on the fly.

**Acceptance criteria / DoD**

- No provider-config response or log line ever contains a secret value.
- A provider-config `PUT` leaves the existing secret untouched (it is not a field on that boundary).
- Rotating a secret is possible at runtime through the credential API alone.

### 5. Credentials are provider-keyed and settings-backed; the database is optional

Credentials become a **settings-backed, provider-keyed collection** that works with **no database**. The
existing PostgreSQL/EF Core backend is retained as an **opt-in** (when a connection string is present) for
operators who want encrypted-at-rest persistence; otherwise an in-memory settings store is the default, and
its writes succeed rather than silently failing.

**Acceptance criteria / DoD**

- With no connection string configured, credential create/update/activate/delete all succeed in-memory.
- A credential is addressable by the named provider it serves, not by dialect alone.
- When a connection string is configured, the encrypted database backend behaves as in HLD 002.

### 6. Provider-addressable authorization-override and activation

The passthrough authorization-override and credential activation become **provider-addressable**, e.g.
`/routing/{dialect}/{provider}/override-authorization`. A request that names only a dialect (no provider)
resolves to that dialect's **default** provider. The inbound proxy URLs (`/openai/...`, `/anthropic/...`) are
unchanged — provider addressing is an admin-surface concern only.

**Acceptance criteria / DoD**

- An override/activation can target one named provider when a dialect has several.
- A dialect-only override/activation applies to the dialect's default provider.
- The inbound proxy routing contract (model-mapping → default) is unchanged.

## Core Separation of Concerns

> Configuration is **runtime state seeded from config**, not state frozen at boot — and it is exposed through
> two boundaries over one registry: *routing config* (secret-free) and *credentials* (secret-only).

Environment and `appsettings` describe the **baseline** the registry boots from; from then on the admin API
is authoritative until restart. The provider-config boundary owns dialect, URL, scheme, mappings, default,
normalization, and the `Enabled` flag — never the secret. The credential boundary owns the secret and its
outbound auth scheme, keyed by the provider it serves. The imposter hot path reads the resulting routes but
is never coupled to how they were mutated.

## Guiding Principle — On the fly, not on redeploy

> Operators reshape routing and rotate secrets through the API; the process never has to restart to follow.

- The registry is owned in-memory by the Application layer; the Host only binds the baseline and maps
  endpoints.
- We will **not** persist the runtime registry (a restart deliberately reseeds from config + env), **not**
  expose secrets on the routing-config boundary, and **not** alter the imposter hot-path contract or the
  statelessness guarantee from HLD 001.

---

## Diagrams

- [System Context (C1) + supporting diagrams](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–06 are strategic (*what* and *why*); LADR-07 is tactical (*how*). Each is a single decision — a
horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-runtime-mutable-registry.md) | Runtime-mutable provider registry over `IOptionsSnapshot`, in-memory and not persisted | Draft |
| [LADR-02](./ladrs/LADR-02-config-secret-boundaries.md) | Two boundaries over the registry — routing config (secret-free) vs credentials (secret-only) | Draft |
| [LADR-03](./ladrs/LADR-03-enabled-flag.md) | `Enabled` flag per provider; disabled providers are excluded from resolution | Draft |
| [LADR-04](./ladrs/LADR-04-runtime-wins-over-env.md) | Runtime CRUD wins; environment overrides only seed the registry at startup | Draft |
| [LADR-05](./ladrs/LADR-05-settings-backed-provider-keyed-credentials.md) | Credentials settings-backed and provider-keyed; database backend optional | Accepted |
| [LADR-06](./ladrs/LADR-06-provider-addressable-override.md) | Provider-addressable authorization-override and activation; dialect-only → default | Accepted |
| [LADR-07](./ladrs/LADR-07-snapshot-consumption-lifetime.md) | Consume options via `IOptionsSnapshot` (scoped); rebuild the catalog per request scope | Draft |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable target, a verification
mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-mutation-visibility.md) | Consistency | A successful write is observed by the next inbound request | Draft |
| [NFR-02](./nfrs/NFR-02-secret-confidentiality.md) | Security | No secret value in any config response or log line | Draft |
| [NFR-03](./nfrs/NFR-03-admin-authorization.md) | Security | Every mutating endpoint requires the admin key (401/403 otherwise) | Draft |
| [NFR-04](./nfrs/NFR-04-ephemerality.md) | Operability | Runtime mutations are in-memory; restart reseeds deterministically from config + env | Draft |
| [NFR-05](./nfrs/NFR-05-hotpath-parity-no-db.md) | Compatibility | Imposter hot-path parity preserved; full CRUD + override work with no database | Draft |
