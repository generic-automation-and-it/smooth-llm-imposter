# Architecture

The router is a stateless ASP.NET Core minimal-API service that sits between an OpenAI- or
Anthropic-dialect client and the real LLM providers. This page is the canonical home for the
tech stack and the project layout — the [root README](../..) links here for those details.

## Tech Stack

| Component | Technology |
|---|---|
| Framework | ASP.NET Core minimal APIs (.NET 10) |
| Architecture | Clean Architecture — `Domain` / `Application` / `Infrastructure` / `Host` |
| Forwarder | `IHttpClientFactory` streaming proxy — no DB, no Mediator / FluentValidation pipeline (see [LADR-001](../hld/001-llm-imposter-routing/ladrs/LADR-001-no-mediator-no-fluentvalidation.md), [LADR-002](../hld/001-llm-imposter-routing/ladrs/LADR-002-stateless-no-ef-postgresql.md)) |
| Persistence | None for routing; PostgreSQL only for the optional credential-admin API |
| Logging | Serilog |
| Testing | xunit.v3 · Shouldly · Bogus |

This repo also ships an **AI-agent scaffold** under [`.agents/`](../../agents/) — one source of truth
for Claude Code, GitHub Copilot, Cursor, and OpenAI Codex (skills, rules, hooks). After cloning, run
`bash .agents/setup/scripts/agents-setup.sh` once so the agents can discover it. See
[`AGENTS.md`](../../AGENTS.md).

## Project Structure

```
src/
  SmoothLlmImposter.Domain/          # Routing value objects + matcher (ApiDialect, ProviderRoute, ModelMatcher)
  SmoothLlmImposter.Application/      # Features/Routing — options, catalog, resolver, transformers, router
  SmoothLlmImposter.Infrastructure/  # UpstreamForwarder over IHttpClientFactory (no DB)
  SmoothLlmImposter.Host/            # Minimal-API dialect endpoints, options binding + ValidateOnStart, Serilog

tests/
  SmoothLlmImposter.{Domain,Application,Infrastructure,Host}.UnitTest/   # L0 — no I/O, in-process
  SmoothLlmImposter.Host.IntegrationTest/                                 # L2 — real Host, stubbed upstream
  SmoothLlmImposter.TestFramework/                                        # Shared fixtures
```

For the authoritative per-layer context, see the `*_AGENTS.md` next to each project:

| Layer | Context file |
|---|---|
| Domain | [`src/SmoothLlmImposter.Domain/DOMAIN_AGENTS.md`](../../src/SmoothLlmImposter.Domain/DOMAIN_AGENTS.md) |
| Application | [`src/SmoothLlmImposter.Application/APPLICATION_AGENTS.md`](../../src/SmoothLlmImposter.Application/APPLICATION_AGENTS.md) |
| Infrastructure | [`src/SmoothLlmImposter.Infrastructure/INFRASTRUCTURE_AGENTS.md`](../../src/SmoothLlmImposter.Infrastructure/INFRASTRUCTURE_AGENTS.md) |
| Host | [`src/SmoothLlmImposter.Host/HOST_AGENTS.md`](../../src/SmoothLlmImposter.Host/HOST_AGENTS.md) |
| Test framework | [`tests/SmoothLlmImposter.TestFramework/TEST_FRAMEWORK_AGENTS.md`](../../tests/SmoothLlmImposter.TestFramework/TEST_FRAMEWORK_AGENTS.md) |
