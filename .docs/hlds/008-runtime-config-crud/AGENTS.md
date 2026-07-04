# AGENTS.md - Runtime Config CRUD & Provider-Addressable Credentials

AI Context: HLD for Runtime Config CRUD & Provider-Addressable Credentials. Updated: 2026-07-04

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

Makes the named-provider registry (HLD 007) runtime-mutable via an admin API and re-bases credentials
(HLD 002) on settings with an optional DB. Intent in [`./README.md`](./README.md), decisions in
[`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/).

## Non-Negotiables

- Do **not** put the secret on the provider-config boundary — routing config (secret-free) and credentials
  (secret-only) are deliberately split ([LADR-02](./ladrs/LADR-02-config-secret-boundaries.md)). A
  provider-config `GET` must round-trip into `PUT` with no secret field.
- Do **not** keep the catalog/resolver as process-lifetime singletons — runtime mutations must be visible on
  the next request via a scoped catalog reading the current `IProviderRegistry` snapshot directly
  ([LADR-01](./ladrs/LADR-01-runtime-mutable-registry.md),
  [LADR-07](./ladrs/LADR-07-snapshot-consumption-lifetime.md)).
- Do **not** persist the runtime provider registry; restart reseeds from config + env
  ([NFR-04](./nfrs/NFR-04-ephemerality.md)). Only the opt-in credential DB persists.
- Do **not** require a database for any admin operation — the prior silent no-op store is removed
  ([LADR-05](./ladrs/LADR-05-settings-backed-provider-keyed-credentials.md)).
- Do **not** add a provider segment to the inbound proxy URLs (`/openai/...`, `/anthropic/...`) — provider
  addressing is admin-surface only ([LADR-06](./ladrs/LADR-06-provider-addressable-override.md)).
- This HLD is **Completed**: all LADRs (01–07) and NFRs (01–05) are Accepted and implemented across Phase 1
  (#49) and Phase 2 (#50). Treat the decisions as binding — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-01 | Runtime-mutable in-memory registry read by scoped catalog consumers | A once-built singleton can never see a mutation |
| LADR-02 | Routing-config vs credential boundaries | Putting the secret on the config boundary leaks it |
| LADR-03 | `Enabled` flag; disabled providers excluded from resolution | Forgetting it in default-selection re-enables a parked route |
| LADR-04 | Runtime CRUD wins; env seeds only at startup | A per-request env post-configure would revert runtime edits |
| LADR-05 | Settings-backed, provider-keyed credentials; DB optional | Dialect keying can't disambiguate multiple providers; DB must not be required |
| LADR-06 | Provider-addressable override/activation; dialect-only → default | Dialect-only keying can't target one of several providers |
| LADR-07 | Read `IProviderRegistry` from a scoped catalog; rebuild catalog per scope | Capturing a route catalog in a singleton breaks visibility |

## Key Behaviors

- Disabling the only default for a dialect leaves passthrough with no default → fail-closed 404 until another
  default is enabled (consistent with today's "no default configured").
- A matched imposter route never reads the credential store or override switch (HLD 001 / HLD 002 LADR-004
  parity); credentials enter only on the passthrough branch.
- A dialect-only override/activation resolves to the dialect's default provider.
- Activation is per provider: at most one active credential per `(dialect, providerName)`, not per dialect.

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- Secret values never appear in any config/credential response or log line
  ([NFR-02](./nfrs/NFR-02-secret-confidentiality.md)).
- Every `/admin/*` and `/routing/*` control endpoint enforces the existing admin policy
  ([NFR-03](./nfrs/NFR-03-admin-authorization.md)).

## Migration Plans

- HLD 002 credential model (mandatory PostgreSQL, dialect-keyed) is superseded by the settings-backed,
  provider-keyed model; the DB is now an opt-in backend. HLD 003 override is provider-addressable. HLD 002
  carries supersession notes and HLD 003 is Completed — those statuses are already synced.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-21 | HLD scaffolded | #48 |
| 2026-06-21 | Phase 2 provider-keyed credentials and provider-addressable override implemented; LADR-05/06 accepted. | #50 |
| 2026-06-21 | HLD Completed: both phases shipped; all LADRs (01–07) and NFRs (01–05) Accepted. | #50 |
| 2026-07-04 | Amended LADR-01/07 implementation detail: scoped catalog reads `IProviderRegistry` directly instead of per-request options binding. | — |
