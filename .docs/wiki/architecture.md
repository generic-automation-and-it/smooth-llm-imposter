# Architecture

## Tech Stack

| Component | Technology |
|---|---|
| Framework | ASP.NET Core minimal APIs (.NET 10) |
| Architecture | Clean Architecture — `Domain` / `Application` / `Infrastructure` / `Host` |
| Forwarder | `IHttpClientFactory` streaming proxy — no DB, no Mediator / FluentValidation pipeline (see [LADR-001](../hld/001-llm-imposter-routing/ladrs/LADR-001-no-mediator-no-fluentvalidation.md), [LADR-002](../hld/001-llm-imposter-routing/ladrs/LADR-002-stateless-no-ef-postgresql.md)) |
| Persistence | None for routing; PostgreSQL only for the optional credential-admin API |
| Logging | Serilog |
| Testing | xunit.v3 · Shouldly · Bogus |

This repo also ships an **AI-agent scaffold** under [`.agents/`](../../.agents/) — one source of truth for Claude
Code, GitHub Copilot, Cursor, and OpenAI Codex (skills, rules, hooks). After cloning, run
`bash .agents/setup/scripts/agents-setup.sh` once so the agents can discover it. See [`AGENTS.md`](../../AGENTS.md).

---

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
