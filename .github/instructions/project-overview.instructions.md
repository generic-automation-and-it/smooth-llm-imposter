---
description: 'Project backend tech stack, architecture, and commands for AI coding tasks'
globs: "**"
paths:
  - "**"
applyTo: '**'
alwaysApply: true
---
# Project Overview

Updated: 2026-05-09

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Framework | ASP.NET Core (.NET 10) |
| Architecture | Clean Architecture (`Domain` / `Application` / `Infrastructure` / `Host`) |
| API style | Minimal API endpoints in `src/Project.Host` |
| Mediator | [`martinothamar/Mediator`](https://github.com/martinothamar/Mediator) (in-process request/response dispatch with pipeline support) |
| Messaging durability options | Message Queue / Message Streaming can be introduced when durability, retries, or asynchronous decoupling are required |
| Validation | FluentValidation in Mediator pipeline (fail fast) |
| Persistence | EF Core + PostgreSQL (`Npgsql.EntityFrameworkCore.PostgreSQL`) |
| Logging/Observability | Serilog + OpenTelemetry |
| Testing | xunit.v3 + Shouldly + Bogus + Respawn |

## Commands

```bash
dotnet build Project.slnx
dotnet test Project.slnx

# Targeted test projects
dotnet test tests/Project.Domain.UnitTest
dotnet test tests/Project.Application.UnitTest
dotnet test tests/Project.Infrastructure.UnitTest
dotnet test tests/Project.Host.UnitTest
dotnet test tests/Project.Application.ComponentTest
dotnet test tests/Project.Infrastructure.ComponentTest
dotnet test tests/Project.Host.IntegrationTest
```

## Project Structure

```
src/
  Project.Domain/          # Entities, value objects, invariants
  Project.Application/     # Feature slices + Mediator handlers/pipelines
  Project.Infrastructure/  # EF Core persistence + external integrations
  Project.Host/            # Minimal API composition, middleware, observability

tests/
  Project.*.UnitTest/
  Project.*.ComponentTest/
  Project.*.IntegrationTest/
  Project.TestFramework/
```

## AI Coder Rules (Summary)

- Keep business logic out of `Host`; route requests into Application via Mediator.
- In Application, organize by `Features/<FeatureName>/` (no global `Commands/` or `Queries/` folders).
- Use `Mediator` (martinothamar) — not `MediatR`.
- Add a FluentValidation validator for each request model and enforce validation in a fail-fast Mediator pipeline.
- If a use case suggests durable/asynchronous processing, explicitly prompt whether to introduce Message Queue or Message Streaming before generating that integration.
- Update the closest `*_AGENTS.md` context file in each PR.

## Changelog

> AI loading note: Skip this section during routine task execution. Use it only when updating this rule file.

| Date | Change |
|:-----|:-------|
| 2026-05-30 | Initial version. |
