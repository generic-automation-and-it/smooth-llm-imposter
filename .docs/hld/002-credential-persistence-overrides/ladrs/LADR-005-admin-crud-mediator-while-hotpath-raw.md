# LADR-005 — Admin CRUD via Mediator/FluentValidation; hot path stays raw

- **Date / Status:** 2026-06-15 · Accepted

## Context

[HLD 001 LADR-001](../../001-llm-imposter-routing/ladrs/LADR-001-no-mediator-no-fluentvalidation.md) deliberately
keeps the routing endpoints free of Mediator and the FluentValidation request pipeline: proxy bodies are
opaque pass-through, validation is on configuration at startup, and the hot path must stay lean. The new
admin endpoints are the opposite kind of work — structured CRUD with real request models to validate.

## Decision

Implement the credential admin endpoints with the **project-standard stack**: minimal-API endpoints in Host
that mediate into `Application/Features/Credentials/` via `martinothamar/Mediator`, each request backed by a
FluentValidation validator in a fail-fast pipeline (per the backend `api-mediator-validation` rule). The
**routing hot path remains raw** exactly as HLD 001 LADR-001 specifies — no Mediator, no request validation
pipeline there.

## Consequences

- Two request styles coexist intentionally: raw streaming forwarder (routing) and Mediator + FluentValidation
  (admin CRUD). This is not an inconsistency to "fix" — it is the documented split.
- Admin slices follow the standard feature-slice shape (request / response / handler / validator nested per
  feature), keeping business logic out of Host.
- HLD 001 LADR-001 is **not** superseded; it is narrowed to "the routing path" rather than "the whole service."
