# Passthrough Authorization Override — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Owner** | TBD (resolve before Accepted) |
| **Tracker** | TBD ([NO-TICKET] at time of authoring) |
| **Last updated** | 2026-06-17 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are building
> and why, the decisions behind it, and the quality bar it must meet. It does **not** contain an
> implementation plan; execution (phasing, sub-issues, sequencing) is tracked in the issue/work
> tracker.
>
> **Builds on HLD 002.** This reuses the stored-credential model and the passthrough credential
> seam from [HLD 002 — Credential Persistence & Overrides](../002-credential-persistence-overrides/README.md)
> and **reaffirms [HLD 002 LADR-004](../002-credential-persistence-overrides/ladrs/LADR-004-overrides-passthrough-only.md)**
> (overrides are passthrough-only). It adds one runtime switch on top; it changes no data model.

## Intent

An operator running Claude Code / an OpenAI SDK through the router on the **passthrough** path
sometimes needs the upstream to receive a `Bearer` token instead of the `x-api-key` the caller
(or the stored credential's own scheme) would otherwise produce — e.g. an upstream gateway that
only accepts `Authorization: Bearer`. Today that requires storing/activating a credential with
exactly the right scheme. We want a **terminal-friendly runtime toggle**, flippable with one
parameterless `curl` per dialect, that forces the passthrough path to drop `x-api-key` and send
the dialect's **active** stored credential as `Authorization: Bearer`. The switch lives only in
memory (a restart forgets it) and never touches the imposter hot path.

## Key Goals

### 1. A parameterless, per-dialect runtime toggle

Expose `PUT /routing/{dialect}/override-authorization` to **enable** the override for a dialect and
`DELETE /routing/{dialect}/override-authorization` to **disable** it, where `{dialect}` is
`anthropic` or `openai`. The verbs are honest: `PUT` *sets* the override on, `DELETE` removes it —
not a `GET`, which only reads. Both are driven by a bare `curl` with no query string and no body,
authenticated with the existing `X-Admin-Api-Key` header. A `GET` on the same path reports the
current on/off state so the toggle is observable.

**Acceptance criteria / DoD**

- `curl -X PUT  -H "X-Admin-Api-Key: …" …/routing/anthropic/override-authorization` enables it for Anthropic.
- `curl -X DELETE -H "X-Admin-Api-Key: …" …/routing/openai/override-authorization` disables it for OpenAI.
- Each dialect is toggled independently; an unknown `{dialect}` is rejected (`400`/`404`), not silently accepted.
- No request body or query parameter is required or read.

### 2. Force `Authorization: Bearer` from the active credential — passthrough only

When the override is **ON** for a dialect, the **passthrough / default** branch (the no-imposter-match
path from [HLD 001 LADR-005](../001-llm-imposter-routing/ladrs/LADR-005-no-default-passthrough-type-only.md))
forwards using the dialect's **active** stored credential (HLD 002 `IsActive`), presenting the
decrypted secret as `Authorization: Bearer …` and **omitting `x-api-key` entirely** — regardless of
that credential's stored `AuthScheme`. When the override is **OFF** (the default), behaviour is
exactly HLD 002: the active credential is applied with its own `AuthScheme`, or the config key is used
if there is no active credential.

**Matched imposter routes are never affected** — they keep using the provider's configured key and
never read the database or the switch. If a request is deviated to an imposter route, it uses the
existing config auth, full stop.

**Acceptance criteria / DoD**

- With the override ON, an outbound passthrough request carries `Authorization: Bearer <secret>` and **no** `x-api-key` header.
- A stored credential whose `AuthScheme` is `ApiKey` is still sent as `Bearer` while the override is ON.
- With the override OFF, forwarding is byte-for-byte the HLD 002 behaviour.
- A matched imposter request is identical with the override ON or OFF (config key, no DB read).

### 3. Fail-safe and guarded

The switch defaults to **OFF** and resets to OFF on restart (no persisted state). Enabling an override
for a dialect that has **no active stored credential** is refused with `403` — you cannot arm a switch
that has nothing to present. If the override is ON but the active credential is removed afterwards, the
passthrough request **fails closed** (a dialect-shaped auth error) rather than silently reverting to the
`x-api-key`/config key it was meant to suppress.

**Acceptance criteria / DoD**

- `PUT …/override-authorization` returns `403` when the dialect has no active credential, and the switch stays OFF.
- After a process restart, every dialect's override is OFF.
- Override ON + no active credential at request time → the passthrough request returns a dialect-shaped error, never an `x-api-key` request.

## Core Separation of Concerns

> The override is a runtime **forwarding-auth presentation** switch on the passthrough seam — it changes
> *how* the active credential's secret is presented (forced `Bearer`, no `x-api-key`), never *which* routes
> are eligible and never the imposter hot path.

It reuses HLD 002's credential store and its single passthrough seam unchanged; it adds neither a new entity
nor a new persisted column. The only new state is an in-memory, per-dialect boolean that an operator flips
over HTTP and the process forgets on restart.

## Guiding Principle — A curl flips it; a restart forgets it; the imposter path never feels it

> One header-authenticated `curl`, no parameters, per dialect — and it stops at the passthrough door.

- The switch owns exactly one decision: *force Bearer from the active credential on passthrough, or not*.
- It will **not** be persisted, will **not** select credentials (the active credential is chosen via HLD 002's
  `activate`), and will **not** be readable or writable by the unauthenticated routing surface.
- It will **never** alter a matched imposter route, add a per-request DB read to the hot path, or fall back to
  `x-api-key` once armed.

---

## Diagrams

- [System Context (C1) + Override-gated passthrough flow + Toggle/forward sequence](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 001–003 are strategic (*what* and *why*); 004–005 are tactical (*how*). Each is a single decision —
a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-001](./ladrs/LADR-001-in-memory-runtime-override-switch.md) | Override state is an in-memory, per-dialect switch — never persisted | Draft |
| [LADR-002](./ladrs/LADR-002-force-bearer-from-active-credential.md) | ON forces `Bearer` from the **active** credential, dropping `x-api-key` | Draft |
| [LADR-003](./ladrs/LADR-003-passthrough-only-imposter-untouched.md) | Override applies to passthrough only; imposter routes keep config auth | Draft |
| [LADR-004](./ladrs/LADR-004-put-delete-routing-endpoint-admin-authed.md) | `PUT`/`DELETE /routing/{dialect}/override-authorization`, admin-authed | Draft |
| [LADR-005](./ladrs/LADR-005-403-no-active-credential-fail-closed.md) | `403` when arming with no active credential; fail closed at request time | Draft |

## Non-Functional Requirements

Each NFR is a horizontal quality concern with a measurable target, a verification mechanism, and acceptance
criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-001](./nfrs/NFR-001-no-apikey-leak-secret-handling.md) | Security | Override ON ⇒ outbound passthrough has `Bearer`, never `x-api-key`; secret never logged | Draft |
| [NFR-002](./nfrs/NFR-002-toggle-endpoint-authorization.md) | Security / AuthZ | Toggle endpoints admin-only: anon `401`, non-admin `403` | Draft |
| [NFR-003](./nfrs/NFR-003-toggle-observability-operability.md) | Operability | Toggles logged (no secret); switch read is in-memory O(1), zero hot-path DB cost; OFF on restart | Draft |

## Out of scope (for now)

Persisting the switch across restarts; applying the override to **matched imposter** routes (intentionally
excluded — see [LADR-003](./ladrs/LADR-003-passthrough-only-imposter-untouched.md)); per-request override via
caller header; selecting *which* credential is active (that stays HLD 002's `activate`); forcing schemes other
than `Bearer`; and multi-tenant / per-caller override isolation.
