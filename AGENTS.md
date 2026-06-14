# AGENTS.md

This file provides guidance for AI coding agents working in the Project repository.

> **⚠️ TEMPLATE NOTICE — read before working.**
> This repository is currently a **template**. `Project` is a placeholder name used throughout the `.agents` tree, root `AGENTS.md`, rule files, skill docs, and the solution/project layout (`Project.slnx`, `src/Project.*`, `tests/Project.*`).
>
> **As soon as the project is given a real name, you MUST:**
> 1. Replace every occurrence of `Project` / `project` (PascalCase namespaces/paths and lowercase prose) across all `.agents` files, root `AGENTS.md`, rule files, skill docs, `Project.slnx`, and the GitHub URL/slug with the chosen name.
> 2. Update the glossary and the Project Overview description below to describe the real project.
> 3. **Remove this entire TEMPLATE NOTICE block** — including this instruction — once the rename is complete.

## Project Overview

Project is an AI-spec-driven, AI-agnostic development project. It documents reusable patterns, blueprints, and component specifications that guide automated and AI-assisted software delivery. _(Placeholder description — update once the project is named; see Template Notice above.)_

**Tech stack:** .NET 10 · ASP.NET Core · Clean Architecture (Domain / Application / Infrastructure / Host) · EF Core + PostgreSQL · Mediator (source-gen CQRS) · xunit.v3

## AI Context Files

Keep `*_AGENTS.md` files synchronised with code and documentation changes. Functional `*_AGENTS.md` files in feature folders are auto-loaded by the `load-agents-context` PostToolUse hook on the first Read/Edit in their directory tree — no manual registration required.

### Required Maintenance

- Every PR should create or update at least one `*_AGENTS.md` file.
- Update the closest context file to the code you change. Prefer local context over adding more content to this root file.
- When domain model or structural shape changes, also update the relevant implementation or architecture context.

### Placement Rules

- Functional feature context belongs close to the feature code.
- Cross-cutting concerns belong under `.docs/hlds/02-nfrs/` or the nearest `*_AGENTS.md`.
- Avoid creating duplicate context files that restate the same plan at multiple levels without adding new information.

## Implementation Docs

All planned work is tracked as worktasks under `.context/work-tasks/` (gitignored — local only). Use `/create worktask` to scaffold a new one from the template.

## Repository Layout (Navigation)

| Layer | Path | Purpose |
|---|---|---|
| Domain | `src/Project.Domain/` | Core entities, value objects — no external deps |
| Application | `src/Project.Application/` | Vertical-slice use cases via Mediator — `Features/<Name>/`, shared code in `Common/` |
| Infrastructure | `src/Project.Infrastructure/` | EF Core + PostgreSQL (`Persistence/`), HTTP clients (`Clients/`) |
| Host | `src/Project.Host/` | ASP.NET Core Web API, Serilog, Scalar OpenAPI |
| ChatHost | `src/Project.ChatHost/` | Standalone LLM microservice — owns Anthropic SDK; talks to Host via HTTP only |

Detailed backend coding rules are maintained in `.agents/rules/backend/` and scoped per-file via frontmatter (see Rules section).

## Rules

All rules live under `.agents/rules/` as `*.instructions.md` files and are auto-loaded every session by Claude Code, Cursor, Copilot, and Codex via the symlinks/path-references documented in `.agents/AI_DEVELOPMENT_AGENTS.md`. Applicability is scoped **per-file** via frontmatter (`paths` for Claude, `globs`+`alwaysApply` for Cursor, `applyTo` for Copilot) — e.g. backend rules carry `**/*.cs` so they attach when a C# file is opened. Rules are organized into category subfolders for navigation; the folder is organizational only and does not change loading. One exception to "auto-loaded every session": prompt-scoped rules may be **deferred for Claude** and re-injected on demand by a `UserPromptSubmit` hook (e.g. `code-review-standards` loads only on review prompts via `.agents/hooks/code-review-standards-context.sh`; Cursor/Copilot still load it always). See `.agents/rules/meta/rules.instructions.md` ("Hook-deferred rules") for the file convention and `.agents/skills/manage-rule-system/SKILL.md` for the directory contract.

### Rule Categories

| Category | Folder | Contents |
|----------|--------|----------|
| _(cross-cutting)_ | `.agents/rules/` (flat) | `ai-workflow-rules`, `code-review-standards` (Claude: hook-deferred to review prompts), `project-overview` |
| git | `.agents/rules/git/` | `git-policy`, `pr-standards` |
| meta | `.agents/rules/meta/` | `rules` (file convention), `knowledge-conventional-contexts-quality` (AGENTS.md quality) |
| backend (`**/*.cs`) | `.agents/rules/backend/` | `api-mediator-validation` (Minimal API + Mediator + FluentValidation fail-fast); `architecture-slices` (clean-architecture boundaries, vertical-slice Features); `backend-logging-conventions` (Information vs Debug levels); `external-api-clients` (Refit list vs singular client split, HybridCache adapter); `migrations` (`[ExcludeFromCodeCoverage]` requirement); `wiremock-stubbing` (TestFramework.Aspire single-source stub helper) |

## Build / Test Commands

```bash
dotnet build Project.slnx                     # build
dotnet test  Project.slnx                     # run all tests
dotnet run --project src/Project.AppHost      # dev Aspire AppHost
dotnet run --project src/Project.ChatHost     # ChatHost standalone (separate process from the API Host)
```

Target a single test project directly when needed (e.g. `dotnet test tests/Project.Domain.UnitTest`); `ls tests/` lists them — no Trait annotations required. **Gotcha:** the dev Aspire dashboard runs at `http://localhost:15278`; when started from a terminal, use the printed `/login?t=...` URL on first browser visit.

## Test Framework

xunit.v3 · Shouldly · Bogus · Respawn. Three tiers (the distinction is non-obvious and drives where a test belongs):

- **L0** `*.UnitTest` — no I/O, all in-process.
- **L1** component — `Application.ComponentTest` uses in-memory EF Core; `Infrastructure.ComponentTest` uses a real isolated DB + Respawn.
- **L2** `*.IntegrationTest` — full stack, real PostgreSQL.

Shared fixtures live in `tests/Project.TestFramework/`; the Aspire dependency host (PostgreSQL + WireMock containers) in `tests/Project.TestFramework.Aspire/`. See `.docs/wiki/testing.md`.

## Style and Dependencies

Authoritative stack and coding conventions for AI coders are in `.agents/rules/project-overview.instructions.md` and backend-specific rules under `.agents/rules/backend/` (scoped per-file via `**/*.cs` frontmatter).

## Architecture Decisions (NFRs)

Human-facing reviewer documentation lives in `.docs/wiki/`. Detailed high-level designs, non-functional requirements, and lightweight architecture decision records live under `.docs/hlds/`.

## CI/CD

PR gate — `.github/workflows/pr-gate.yml` (triggers: `pull_request` → `main`, `push` → `main`, `workflow_dispatch`): restore → build (Release) → Aspire-backed test with coverage via the local action `.github/actions/aspire-test-with-coverage`, then publish + upload the coverage report. Full step list, service ports, timing, and local .NET tools: `.docs/wiki/ci.md`.

## Git Constraints

This repository is hosted on **GitHub** at `https://github.com/generic-automation-and-it/project`.

- **CLI tool:** Use `gh` (GitHub CLI) for PR and repository operations.
- **PR template:** `.github/pull_request_template.md`
- **Code owners:** `.github/CODEOWNERS` — all files owned by `@generic-automation-and-it/project`

## Glossary

<!-- TODO: Add domain-specific terms and abbreviations as the project evolves. -->

| Term | Description |
|---|---|
| Blueprint | A reusable, parameterised specification for a component or service |
| Catalogue | The collection of all blueprints and templates in this repository |
| Spec-driven | Development approach where machine-readable specifications are the source of truth |
