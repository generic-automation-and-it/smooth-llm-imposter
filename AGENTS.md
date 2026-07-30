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
Conductor sandbox setup docs should keep general snapshot tooling (including Docker CLI + Compose v2) separate
from SmoothLlmImposter-specific imposter runtime setup. Agent-tooling steps must be split by scope: anything
repository-scoped (a tool that records an absolute repo path in its config, or writes into the working tree)
belongs in the workspace lifecycle, because the snapshot is built before any clone exists.
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

Container builds intentionally mirror the repo-root layout inside the SDK stage (`src/SmoothLlmImposter.*` under a non-`/src` working directory). When editing `Dockerfile`, keep `SmoothLlmImposter.slnx`, `Directory.*.props`, `NuGet.Config`, and `src/` in their repo-root-relative positions so solution/project references and central package props continue to resolve.

## Test Framework

xunit.v3 · Shouldly · Bogus. Tiers (the distinction drives where a test belongs):

- **L0** `*.UnitTest` — no I/O, all in-process (Domain / Application / Infrastructure / Host).
- **L2** `SmoothLlmImposter.Host.IntegrationTest` — boots the real Host in-process via `WebApplicationFactory` and swaps the `imposter-upstream` HTTP client for a stub transport. No DB, no containers — this router is stateless and key-less.

Shared fixtures live in `tests/SmoothLlmImposter.TestFramework/`. CI provisions a single WireMock service container (`127.0.0.1:19091`) for integration tests that stub upstream LLM endpoints over HTTP. See `.docs/wiki/testing.md`.

## Style and Dependencies

Authoritative stack and coding conventions for AI coders are in `.agents/rules/project-overview.instructions.md` and backend-specific rules under `.agents/rules/backend/` (scoped per-file via `**/*.cs` frontmatter).

## Architecture Decisions (NFRs)

Human-facing reviewer documentation lives in `.docs/wiki/`. Detailed high-level designs, non-functional requirements, and lightweight architecture decision records live under `.docs/hlds/`.

The [`README.md` → How it works](README.md#how-it-works) **HLD table is the human-facing index** for `.docs/hld/`. **Keep it in sync** — when a new HLD is created, removed, or changes status (Discovery → Accepted → Completed, or → Cancelled), update the table in the same PR. A stale table makes the HLD folder harder to discover and contradicts the Drift Minimization rule below.

## CI/CD

PR gate — `.github/workflows/pr-gate.yml` (triggers: `pull_request` → `main`, `push` → `main`, `workflow_dispatch`): restore → build (Release) → test with coverage via the local action `.github/actions/test-with-coverage`, then publish + upload the coverage report. The job declares one WireMock service container (`127.0.0.1:19091`) as its only external dependency — no PostgreSQL/Redis/Aspire. Full step list, service ports, and local .NET tools: `.docs/wiki/ci.md`.

Container image publishing — `.github/workflows/publish-image.yml` pushes `ghcr.io/generic-automation-and-it/smooth-llm-imposter` from the repo-root `Dockerfile`. Keep published tags multi-architecture for both `linux/amd64` and `linux/arm64`; QEMU must be configured before Buildx because the Dockerfile runs `dotnet restore` and `dotnet publish` during target-platform builds. The `:latest` tag is emitted only on an automatic `push` to `main`; a manual `workflow_dispatch` run (allowed from any branch) skips `:latest` and instead publishes a user-supplied pre-release version tag (e.g. `1.0.0-rc.1`) via the required `version` input, plus the short-SHA tag.

**Container base image:** Both `Dockerfile` stages use `-alpine` variants (`.NET 10`); the published image is ~131 MB (~45% smaller than the prior Debian base). Alpine ships `wget` (no `curl`), so the Dockerfile's HEALTHCHECK comment was updated accordingly. Verified multi-platform build still succeeds for `linux/amd64` and `linux/arm64`.

## Git Constraints

This repository is hosted on **GitHub** at `https://github.com/generic-automation-and-it/smooth-llm-imposter`.

- **CLI tool:** Use `gh` (GitHub CLI) for PR and repository operations.
- **Issue/PR target:** create issues and PRs in *this* repo (auto-detected from `git remote`). Do **not** target a different repo unless the user explicitly names it — even when a linked design/tracker issue lives elsewhere.
- **PR template:** `.github/pull_request_template.md`
- **Code owners:** `.github/CODEOWNERS` — all files owned by `@generic-automation-and-it/project` (a GitHub *team* handle, not the repo URL)

## Glossary

<!-- TODO: Add domain-specific terms and abbreviations as the project evolves. -->

| Term | Description |
|---|---|
| Blueprint | A reusable, parameterised specification for a component or service |
| Catalogue | The collection of all blueprints and templates in this repository |
| Spec-driven | Development approach where machine-readable specifications are the source of truth |

## Changelog

- 2026-07-30: Dockerfile switched both stages from Debian to `-alpine` .NET 10 base images (`sdk:10.0-alpine`, `aspnet:10.0-alpine`). Image size dropped from ~240 MB to ~131 MB (~45% reduction). No trimming, self-contained, or GC-mode changes — base-image swap only. Alpine ships `wget` (no `curl`), so the Dockerfile's HEALTHCHECK comment was updated accordingly. Verified with smoke tests.
- 2026-07-30: Added `.code-review-graph/` to `.gitignore` and created `.github/instructions/code-review-graph.instructions.md` for Copilot MCP tool usage.
- 2026-07-29: Conductor workspace setup no longer overrides OpenCode Go session forwarding — it uses the image default (`SessionForwarding: opencode-go`), so matched routes stamp `session_id` / `x-opencode-session`. The per-provider opt-out ships commented out; re-enabling it requires the two exports, both names in `--preserve-env`, and the two `-e` flags. The `-e` flags are documented above the `docker run` rather than inline, because a `#` comment inside a backslash-continued argument list comments out every remaining line.
- 2026-07-29: Conductor setup adds GitHub Copilot CLI and `code-review-graph`. The snapshot installs Python 3.12 + a dedicated venv (a `pip install --user` silently yields a 0-node graph — grammar probes run under `python -I`, which drops user site-packages); the workspace runs the repo-scoped `install --platform codex|copilot-cli` and `build`. `claude-code` is skipped because it rewrites tracked files (`CLAUDE.md`, `.claude/skills`, `.agents/settings.json`).
- 2026-07-25: Conductor build setup removes `OpenAiUpstreamApi=chat_completions` from `opencode-go-openai` — GPT routes now use the Responses API default (no `/responses`→Chat downgrade). **Superseded 2026-07-29** — the image's `appsettings.json` already ships that value, so the change was a redundant-override cleanup, not an API switch; GPT-route behavior (`/v1/chat/completions`) is unchanged.
- 2026-07-29: Conductor build setup correction — the 2026-07-25 entry above was misleading; the `OpenAiUpstreamApi=chat_completions` removal was a redundant-override cleanup (the image default already provides it), not an API switch to Responses. See `src/SmoothLlmImposter.Host/appsettings.json` and `src/SmoothLlmImposter.Application/Features/Routing/ROUTING_AGENTS.md`.
- 2026-07-25: HLD 010 `--who?` and `--newsession` switch family implemented — `--who?` short-circuits the forward path with a dialect-shaped synthetic reply naming the inbound model, resolved upstream (or `passthrough`), auth scheme, and session identity; `--newsession` mints a synthetic session id and stores a caller→synthetic mapping in the `ISessionTranslationDictionary` singleton. Forward-path translation seam added. Diagnostic logging added to `WhoMessageResponder` (non-match reasons) and `RoutingEndpoints` (feature-disabled, translation applied). **Breaking change:** Bare `who?` trigger was replaced by `--who?`. LADRs/NFRs → Accepted; HLD → Completed.
- 2026-07-25: Conductor workspace setup disables OpenCode Go session forwarding (`OPENCODE_GO_ANTHROPIC_SESSION_FORWARDING=none` and `OPENCODE_GO_OPENAI_SESSION_FORWARDING=none`). **Superseded 2026-07-29** — the overrides now ship commented out.
- 2026-07-25: Conductor workspace setup mappings — sonnet/opus-4-6/opus-4-7→opencode-go (qwen3.6-plus/qwen3.7-plus/minimax-m3), haiku→OpenRouter tencent/hy3, gpt-5.4/5.5/5.6-luna→opencode-go (kimi-k2.7-code/glm-5.2/grok-4.5); require OPENROUTER_API_KEY; `openrouter-anthropic` defined fully in script (absent from base image).
- 2026-07-24: Note Conductor sandbox Docker CLI + Compose v2 setup-doc guidance.
- 2026-07-24: Document credential-independent Conductor snapshot preparation and credential-aware workspace container startup.
- 2026-07-30: `publish-image.yml` manual `workflow_dispatch` no longer emits the `:latest` tag — it now publishes a required `version` input (e.g. `1.0.0-rc.1`) as a pre-release tag instead, while `:latest` is reserved for automatic `push` to `main`. Manual dispatch remains available from any branch, so pre-release images can be cut from non-main branches.
