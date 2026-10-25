# AGENTS.md - Passthrough Authorization Override

AI Context: HLD for Passthrough Authorization Override. Updated: 2026-06-21

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

A runtime, in-memory switch, amended by HLD 008 to be provider-addressable
(`PUT`/`DELETE`/`GET /routing/{dialect}/{provider}/override-authorization`, with dialect-only fallback),
that forces the **passthrough** path to send the provider's **active** stored credential as
`Authorization: Bearer`, dropping `x-api-key`. Builds on HLD 002/HLD 008; adds no persisted data model.

## Non-Negotiables

- **Passthrough only.** The switch is read and applied **only** on the no-imposter-match / default branch.
  Matched imposter routes keep the config key, never read the store, and never consult the switch
  (LADR-003, reaffirming HLD 002 LADR-004). Extending the switch to imposter routes is the wrong feature.
- **ON ⇒ `Bearer`, never `x-api-key`.** When ON, force `Authorization: Bearer` from the active credential
  regardless of its stored `AuthScheme`, and omit `x-api-key` (LADR-002). Do not filter to `Bearer`-scheme
  credentials and do not add a "latest credential" query — reuse the existing active-credential lookup.
- **No new persisted state.** The switch is process memory only, default OFF, reset on restart (LADR-001).
  No migration, no entity, no column.
- **Fail closed, never fall back.** Override ON + no active credential at request time ⇒ dialect-shaped auth
  error, **not** a silent revert to `x-api-key`/config key (LADR-005).
- **The control surface is privileged.** Toggle endpoints require `AdminPolicy` (`X-Admin-Api-Key`); the
  proxy `/v1/*` endpoints stay key-less (NFR-002). Use `PUT`/`DELETE` (and `GET` to read) — never a `GET`
  that mutates.
- HLD is Completed (implemented + tested); LADRs are Accepted/load-bearing — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-001 | In-memory switch, default OFF, never persisted (provider-keyed by HLD 008) | Persisting it (or a config flag) changes the operator-visible lifecycle and adds unwanted state/migration |
| LADR-002 | ON forces `Bearer` from the **active** credential, drops `x-api-key` | Honouring the stored scheme or filtering to Bearer-only creds fails the core "remove x-api-key, use bearer" intent |
| LADR-003 | Passthrough only; imposter untouched | Applying it to the imposter hot path breaks HLD 001 determinism and the explicit scope |
| LADR-004 | `PUT`/`DELETE`/`GET /routing/{dialect}/{provider}/override-authorization`, admin-authed; dialect-only fallback | A `GET` that toggles, or an unauthenticated switch, is a verb/security defect |
| LADR-005 | `403` on arm with no active credential; fail closed at request time | Falling back to `x-api-key` silently re-leaks the suppressed scheme |

## Key Behaviors

- `{dialect}` is `anthropic` or `openai`; any other value is rejected (`400`/`404`), not silently accepted.
- Each provider toggles independently; the only new state on the request path is one boolean per provider.
- The switch gates the *existing* HLD 002 active-credential lookup — it adds no new DB read of its own.
- Multi-instance deployments do not share switch state (each instance is toggled independently) — a known v1 limitation, not a bug.

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- The decrypted secret may appear **only** in the outbound `Authorization` header at forward time — never in
  logs, error bodies, or the `GET` state response (NFR-001).
- Each enable/disable emits exactly one secret-free `Information` audit line (dialect + action) (NFR-003).

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-17 | HLD authored — in-memory per-dialect passthrough Bearer override; PUT/DELETE/GET control; passthrough-only; fail-closed. | [NO-TICKET] |
| 2026-06-17 | Implemented per the approved plan (Application switch + slices, Infrastructure force-Bearer, Host admin endpoints, L0+L2 tests). LADRs 001–005 remain **Draft** — recommend promotion to **Prototype** on review (not flipped here). | [NO-TICKET] |
| 2026-06-21 | HLD → **Completed**; LADRs 001–005 + NFRs 001–003 rolled **Draft → Accepted** (shipped + tested). | [NO-TICKET] |
| 2026-06-21 | HLD 008 amended the switch to be provider-addressable with dialect-only → default-provider fallback. | #50 |
