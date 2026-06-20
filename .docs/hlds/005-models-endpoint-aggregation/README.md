# OpenAI /models Endpoint Aggregation — High-Level Design

| | |
|---|---|
| **Status** | Accepted |
| **Owner** | @generic-automation-and-it/project |
| **Tracker** | [feat: /models endpoint returns distinct union of all configured imposter models (#20)](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/20) |
| **Last updated** | 2026-06-20 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker.

## Intent

A client (e.g. Codex) that points its base URL at the router and calls `GET /openai/v1/models`
today receives a single upstream's model list — because the body-less request passes through to
the dialect's default OpenAI provider. That response is an accident of the passthrough path: it
reflects whatever one upstream is configured as default, not the set of models this router can
actually route to. This HLD replaces that behaviour, for the OpenAI dialect only, with a locally
synthesized response: the **distinct union of every `to` model declared across the OpenAI route
catalogue**, shaped as a valid OpenAI `ListModelsResponse`. The router stops calling an upstream
for this path and answers from its own configuration.

## Key Goals

### 1. Aggregate the configured `to` models into the discovery response

`GET /openai/v1/models` returns one OpenAI `Model` object per distinct `to` value found in the
`Models[]` mappings of every OpenAI-dialect provider in the active catalogue. Duplicate `to`
values (the same target declared under more than one provider or mapping) collapse to a single
entry. Providers with no `Models[]` mappings — the default/passthrough providers — contribute
nothing, since they have no `to` to advertise.

The advertised identifier is the **`to`** (upstream target) name, as the issue specifies — this
advertises the real models the imposter forwards to. The trade-off (the inbound `From` name is
what a client must actually send to trigger a route) is recorded and accepted in
[LADR-01](./ladrs/LADR-01-advertise-to-names.md).

**Acceptance criteria / DoD**

- `GET /openai/v1/models` returns a `data` array whose `id` values are exactly the distinct set
  of `to` strings across all OpenAI-dialect provider mappings.
- A `to` value declared under two providers/mappings appears once.
- A catalogue with no OpenAI imposter mappings returns an empty `data` array (still a valid list).
- Default/passthrough providers (no `to`) contribute no entries.

### 2. Serve a schema-valid OpenAI list response with no upstream dependency

The response body is a valid OpenAI `ListModelsResponse`: a top-level `object: "list"` with a
`data` array of `Model` objects each carrying at minimum `id`, `object: "model"`, `created`, and
`owned_by`. The router builds this from configuration alone — it does **not** forward the request
to any upstream and does **not** read the credential store. See
[LADR-02](./ladrs/LADR-02-synthesize-locally.md) and
[LADR-04](./ladrs/LADR-04-synthetic-model-fields.md) for the synthesis and field-value decisions.

**Acceptance criteria / DoD**

- The response deserializes as an OpenAI list-of-models without error.
- Each entry has a non-empty `id`, `object == "model"`, a `created` value, and an `owned_by`.
- Serving the endpoint issues zero outbound upstream requests and opens zero database connections.
- Two calls with identical configuration return byte-identical bodies.

## Core Separation of Concerns

> The `/models` discovery response is a projection of the router's own configuration, not a relay
> of an upstream's catalogue.

Everywhere else in this router the contract is *transparent proxy* — read `model`, forward the
caller's bytes upstream, stream the answer back. Model discovery is the one place where the router
is the authority: it knows the full set of routes, and no single upstream does. This HLD draws a
deliberate seam between *forwarded* traffic (everything with a body, plus any path/method outside
this one case) and the *answered-locally* discovery path, and keeps the seam as narrow as one
dialect, one path, one method.

## Guiding Principle — Answer what you own, forward the rest

> The router answers for the routes it owns; it never invents an upstream's catalogue.

- The discovery list is derived **only** from declared `to` values — no live model fetch, no
  merge with an upstream's list, no synthetic models beyond the configured targets.
- This HLD deliberately does **not** touch the Anthropic dialect, non-GET methods, or any path
  other than `/v1/models` under the `/openai` prefix. Those remain transparent passthrough
  ([LADR-03](./ladrs/LADR-03-openai-get-only.md)).

---

## Diagrams

- [System Context (C1) + Request-decision flow](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01–03 are strategic (*what* and *why*); LADR-04 is tactical (*how*). Each is a single
decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-advertise-to-names.md) | Advertise `to` (upstream target) names, not inbound `From` | Accepted |
| [LADR-02](./ladrs/LADR-02-synthesize-locally.md) | Synthesize the list from the catalogue; replace passthrough, no live upstream call | Accepted |
| [LADR-03](./ladrs/LADR-03-openai-get-only.md) | Scope to OpenAI dialect + `GET /openai/v1/models` only | Accepted |
| [LADR-04](./ladrs/LADR-04-synthetic-model-fields.md) | Synthetic `Model` field values; recognition in Host, aggregation string-out in Application | Accepted |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable target, a
verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-determinism.md) | Determinism | Identical config ⇒ byte-identical response; no time-derived fields | Accepted |
| [NFR-02](./nfrs/NFR-02-schema-conformance.md) | Compatibility | Valid OpenAI `ListModelsResponse`; `data` ids = distinct OpenAI `to` set | Accepted |
| [NFR-03](./nfrs/NFR-03-statelessness.md) | Statelessness | Zero upstream requests and zero DB connections to serve the endpoint | Accepted |
| [NFR-04](./nfrs/NFR-04-secret-confidentiality.md) | Security | Response carries only model ids + provider names; never a `Secret` | Accepted |
