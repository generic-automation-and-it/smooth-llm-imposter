# AGENTS.md - OpenAI /models Endpoint Aggregation

AI Context: HLD for OpenAI /models Endpoint Aggregation. Updated: 2026-06-20

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

`GET /openai/v1/models` is answered locally from the route catalogue (distinct union of OpenAI
`to` model values), replacing the old passthrough — intent in [`./README.md`](./README.md),
decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/).

## Non-Negotiables

- **Advertise `to`, never `From`.** The `id` values are the upstream-target (`to`) names, not the
  inbound trigger (`From`) names — even though that means an advertised id won't itself route as an
  imposter. This is intentional (LADR-01); do not "fix" it by switching to `From`.
- **No upstream call, no DB read on this path.** Synthesize from configuration only. Do not forward
  to the default provider and do not enter the passthrough credential seam (LADR-02, LADR-03).
- **Scope is one case only.** OpenAI dialect + `GET` + post-prefix path `/v1/models`. Anthropic,
  non-GET, and every other path stay transparent passthrough — do not generalize (LADR-03).
- **Never serialize a `Secret`.** The response carries only `to` ids and provider names; provider
  `Secret`/`BaseUrl`/`AuthScheme` must never enter it (NFR-04).
- **`created` is a fixed constant, never wall-clock.** Time-derived fields break determinism (NFR-01).
- **Aggregation is string-out in Application; recognition is in Host.** No `HttpContext` in
  Application; no body-building in the Host endpoint (LADR-04).
- LADRs are Draft status — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-01 | Advertise `to`, not `From` | Switching to `From` silently contradicts the issue AC and changes the contract |
| LADR-02 | Synthesize locally; replace passthrough | A live upstream/DB call reintroduces a dependency the path must not have |
| LADR-03 | OpenAI + GET + `/v1/models` only | Matching other dialects/methods/paths shadows legitimate passthrough |
| LADR-04 | Synthetic fields; Host recognizes, Application aggregates | Wrong field source (wall-clock `created`) or wrong layer breaks NFR-01 and the layering rule |

## Key Behaviors

- A `to` declared under multiple providers/mappings de-dupes to one entry; on dedup, the
  **first declaring provider in catalogue order** supplies `owned_by` (matches first-match-wins).
- An OpenAI catalogue with no mappings returns `object: "list"` with an empty `data` array — still
  a valid response, not a 404.
- Default/passthrough providers (no `Models[]`) contribute nothing — they have no `to`.
- The legacy unprefixed `/v1/models` route stays unmapped (dialect-ambiguous); this behaviour is
  reachable only via the `/openai` prefix.

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- Determinism is testable: two calls under one config must be byte-equal — write the builder so a
  test can assert exact output (no ordering nondeterminism, no time).

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-20 | HLD scaffolded and drafted (README, LADR-01..04, NFR-01..04, C1 + flow diagram). | #20 |
| 2026-06-20 | Implemented: `MapGet("/openai/v1/models")` in Host short-circuits the catch-all to a locally-synthesized OpenAI `ListModelsResponse`; `IModelCatalogResponder` / `OpenAiModelCatalogResponder` in Application aggregates the distinct `to` set (first-declaring-provider `owned_by`, fixed `created=0`, ordinal dedup). L0 + L2 tests cover dedup/order, empty catalogue, byte-stability, no-secret, zero upstream calls, and scope (Anthropic + non-GET passthrough). LADR-01..04 / NFR-01..04 accepted. | #20 |
