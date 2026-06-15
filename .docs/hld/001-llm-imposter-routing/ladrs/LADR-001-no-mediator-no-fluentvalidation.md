# LADR-001 — No Mediator / no FluentValidation request pipeline

- **Date / Status:** 2026-06-14 · Accepted

## Context

The backend rules mandate Mediator dispatch with a per-request FluentValidation pipeline. This
path is a transparent streaming proxy over **opaque** JSON bodies — there is no typed request
model to validate field-by-field, and routing bodies through Mediator adds indirection with no
benefit.

## Decision

Keep the forwarding path out of Mediator. Apply fail-fast validation to **configuration** at
startup (`ImposterOptionsValidator` + `ValidateOnStart`) instead of to requests.

## Consequences

A reviewer expecting the standard slice shape won't find it. Request-level malformations are
surfaced as dialect-shaped 400s by the router, not by a validation pipeline.
