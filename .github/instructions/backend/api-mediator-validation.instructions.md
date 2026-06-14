---
description: 'Backend API rules: minimal APIs in Host, Mediator request flow, and FluentValidation fail-fast pipeline'
globs: "**/*.cs"
paths:
  - "**/*.cs"
applyTo: '**/*.cs'
alwaysApply: false
---

# Backend API + Mediator + Validation Rules

Updated: 2026-05-09

## Non-Negotiables

- Create APIs as ASP.NET Core minimal APIs in `src/Project.Host`.
- Endpoints must mediate to Application via [`martinothamar/Mediator`](https://github.com/martinothamar/Mediator) packages; do not use MediatR.
- Every request/query must pass through a FluentValidation-based Mediator pipeline so invalid input fails fast.
- When generating a new request model, always create a default validator for it (even if initially permissive).
- If a use case indicates durability, retry buffering, or asynchronous decoupling needs, prompt whether to introduce Message Queue or Message Streaming before implementing it.

## Slice Class Shape

Use one parent feature class (or partial class) containing nested types:

- Request/Query type
- Response type
- Handler
- Pipeline(s), including validation pipeline

If the parent file becomes too large, move pipeline types to a `Pipelines/` subfolder but keep them in the same partial parent class.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
