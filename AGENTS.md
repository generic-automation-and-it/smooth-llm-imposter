# AGENTS.md

This file provides guidance for AI coding agents working in the SmoothLlmImposter repository.

> **Note on remaining `Project` placeholders.** This repo was scaffolded from a template. The functional/buildable identifiers (solution, projects, namespaces, folders) have been renamed to `SmoothLlmImposter`. Lowercase `project` references and examples still remaining in `.agents/rules/**` and `.docs/**` prose are deferred template cleanup, not real references — replace them opportunistically when touching those files.

## Project Overview

SmoothLlmImposter is a **stateless, key-less LLM request router**. It exposes OpenAI- and Anthropic-dialect endpoints and, per configuration, redirects an inbound model to an alternate upstream ("imposter") — rewriting the model name and optionally injecting prompt caching — or passes it through to the real provider. Unlike the Smooth Claude Proxy it stores nothing (keys come from config/env only) and routes within a single dialect (no OpenAI⇄Anthropic translation). See `src/SmoothLlmImposter.Application/Features/Routing/ROUTING_AGENTS.md`.

**Tech stack:** .NET 10 · ASP.NET Core minimal APIs · Clean Architecture (Domain / Application / Infrastructure / Host) · `IHttpClientFactory` streaming forwarder (no DB) · Serilog · xunit.v3

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

Setup docs in `.docs/wiki/setup.md` and `.docs/wiki/setups/` should keep client base-url examples aligned with
the run mode's published host port, including Codex `openai_base_url` and `ANTHROPIC_BASE_URL` examples where
the guide is meant to be used by agent clients.
Claude/Anthropic setup sections should also document that `claude setup-token` can create a Claude subscription
token, which users may supply explicitly as an imposter provider `Secret` with the matching `AuthScheme`.

## Repository Layout (Navigation)

| Layer | Path | Purpose |
|---|---|---|
| Domain | `src/SmoothLlmImposter.Domain/` | Pure routing model — `ApiDialect`, `ProviderRoute`, `RouteDecision`, `ModelMatcher` |
| Application | `src/SmoothLlmImposter.Application/` | Routing pipeline in `Features/Routing/` — options, catalog, resolver, transformers, router |
| Infrastructure | `src/SmoothLlmImposter.Infrastructure/` | `UpstreamForwarder` — `IHttpClientFactory` streaming forwarder (no DB) |
| Host | `src/SmoothLlmImposter.Host/` | Minimal-API dialect endpoints, options binding + `ValidateOnStart`, Serilog |

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
dotnet build SmoothLlmImposter.slnx                 # build
dotnet test  SmoothLlmImposter.slnx                 # run all tests
dotnet run --project src/SmoothLlmImposter.Host     # run the router locally
```

Target a single test project directly when needed (e.g. `dotnet test tests/SmoothLlmImposter.Domain.UnitTest`); `ls tests/` lists them. Tests are infra-free (no Docker/DB) — integration tests stub the upstream transport in-process.

## Test Framework

xunit.v3 · Shouldly · Bogus. Tiers (the distinction drives where a test belongs):

- **L0** `*.UnitTest` — no I/O, all in-process (Domain / Application / Infrastructure / Host).
- **L2** `SmoothLlmImposter.Host.IntegrationTest` — boots the real Host in-process via `WebApplicationFactory` and swaps the `imposter-upstream` HTTP client for a stub transport. No DB, no containers — this router is stateless and key-less.

Shared fixtures live in `tests/SmoothLlmImposter.TestFramework/`. CI provisions a single WireMock service container (`127.0.0.1:19091`) for integration tests that stub upstream LLM endpoints over HTTP. See `.docs/wiki/testing.md`.

## Style and Dependencies

Authoritative stack and coding conventions for AI coders are in `.agents/rules/project-overview.instructions.md` and backend-specific rules under `.agents/rules/backend/` (scoped per-file via `**/*.cs` frontmatter).

## Architecture Decisions (NFRs)

Human-facing reviewer documentation lives in `.docs/wiki/`. Detailed high-level designs, non-functional requirements, and lightweight architecture decision records live under `.docs/hlds/`.

## CI/CD

PR gate — `.github/workflows/pr-gate.yml` (triggers: `pull_request` → `main`, `push` → `main`, `workflow_dispatch`): restore → build (Release) → test with coverage via the local action `.github/actions/test-with-coverage`, then publish + upload the coverage report. The job declares one WireMock service container (`127.0.0.1:19091`) as its only external dependency — no PostgreSQL/Redis/Aspire. Full step list, service ports, and local .NET tools: `.docs/wiki/ci.md`.

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
