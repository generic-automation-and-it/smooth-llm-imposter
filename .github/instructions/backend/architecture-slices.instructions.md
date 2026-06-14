---
description: 'Backend architecture rules: clean architecture boundaries and application feature-slice structure'
globs: "**/*.cs"
paths:
  - "**/*.cs"
applyTo: '**/*.cs'
alwaysApply: false
---

# Backend Architecture and Slice Rules

Updated: 2026-05-09

## Non-Negotiables

- Use clean architecture boundaries:
  - `Domain` has core business model and no external dependencies.
  - `Application` coordinates use cases and depends on domain abstractions.
  - `Infrastructure` implements external concerns (DB, clients, providers).
  - `Host` composes the app and maps HTTP endpoints.
- Application code is vertical-slice by feature: `Features/<FeatureName>/`.
- Do not create global `Commands/` or `Queries/` folders in Application.
- Keep business rules out of `Host` and `Infrastructure`; place them in `Domain` or Application use-case orchestration where appropriate.
- Register services through layer extension methods (Host calls `AddApplication()` and `AddInfrastructure()`).

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
