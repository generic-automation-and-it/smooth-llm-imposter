# SmoothLlmImposter

> A stateless, key-less LLM request router. Point your existing OpenAI- or Anthropic-dialect client at it,
> and it transparently redirects chosen models to alternate, cheaper, or local upstreams — rewriting the
> model name and optionally adding prompt caching — or passes the call straight through.

---

## TL;DR

SmoothLlmImposter sits between your LLM client (Claude Code, Codex, any OpenAI/Anthropic SDK) and the real
providers. For each request it reads the `model` and — per your `Imposter` config — either **imposters** it
(rewrites the model to a different upstream, optionally injecting prompt caching) or **passes it through**
unchanged. It is:

- **Stateless & key-less** — nothing about a request is persisted; upstream keys come from config/env only.
- **Dual-dialect** — speaks both OpenAI (`/v1/chat/completions`, `/v1/responses`) and Anthropic
  (`/v1/messages`), selected by a `/openai` or `/anthropic` path prefix. Routing is **same-dialect only**
  (no OpenAI⇄Anthropic translation).
- **Config-driven** — routing is an ordered array of per-provider `From → To` model mappings; first match
  wins. No code change to add, repoint, or remove a route.

There is **no `claude login` and no token capture** inside the Host — you run it with `dotnet`, Docker, or
the published GHCR image, and point your client's base URL at it. Full setup:
**[`.docs/wiki/setup.md`](.docs/wiki/setup.md)**.

---

## Use cases

Run your existing agent client unchanged, and let the router decide what each model call actually hits:

- **Mix subscription + API key across models.** Keep using Claude Code or Codex on your subscription, but
  point selected models at a separate API key — or a different provider entirely. The router applies the
  right `Secret`/`AuthScheme` per provider, so one client transparently spans a subscription *and*
  pay-as-you-go keys.
- **Stretch a small subscription with cheaper models.** On a limited plan, imposter the built-in Claude
  Code / Codex model onto cheaper models from other providers (OpenRouter, opencode, a local upstream, …).
  The client keeps calling its usual model name; the router rewrites it and forwards with the configured
  credentials, so you spend less without touching the client.
- **Switch between work and private subscriptions without re-login.** Configure both — work and private —
  and let the router pick which one backs a given model via config order. You only re-authenticate when a
  subscription's token actually expires, not every time you switch. E.g. the client is pinned to Opus
  4.7 1M but the router rewrites and sends it to your private Opus 4.8 1M.

The same mechanism also enables:

- **Add prompt caching the upstream doesn't.** Set `Caching` per model mapping to inject cache hints for
  upstreams that don't cache themselves — cheaper repeated context, no client change.
- **Route a model to a local/offline upstream.** Point a model's `BaseUrl` at a local server (Ollama,
  LM Studio, llama.cpp, …) for dev, testing, or air-gapped work.
- **Centralise and rotate credentials.** Clients hold no keys; the router injects them from config/env
  only. Rotate or revoke in one place instead of across every client.
- **Migrate or A/B a model centrally.** Repoint a mapping's `To` target to trial a newer/cheaper model —
  or fail over to another provider in the same dialect — by editing config/env. Clients are untouched and
  you can roll back instantly.
- **Pin a stable model alias.** Let clients call one stable model name while you change the concrete model
  behind it over time.

---

## How it works

Inbound dialect is chosen by the endpoint/prefix. The router reads `model`, selects the first matching
provider mapping (config order), rewrites the model, optionally injects caching, applies the provider's
configured `Secret`/`AuthScheme`, and streams the upstream response back unbuffered. An unmatched model
either passes through to the dialect's default provider (forwarding the caller's own credentials) or, when
no default is configured, returns a dialect-shaped 404.

Design detail — actors, request flow, routing decision, NFRs, and decision records — lives in
**[HLD 001 — LLM Imposter Routing](.docs/hld/001-llm-imposter-routing/README.md)**.

---

## Getting Started

### Prerequisites

- **.NET 10 SDK**
- *(Optional)* A container runtime — Docker or Podman — for the image / Compose run modes.
- *(Optional)* **PostgreSQL** — only for the optional `/admin/credentials` passthrough-credential API. Core
  imposter routing needs no database.

### Build & run

```bash
dotnet build SmoothLlmImposter.slnx
dotnet run --project src/SmoothLlmImposter.Host        # -> http://localhost:5080
curl http://localhost:5080/health                      # {"status":"ok"}
```

Then configure the `Imposter` section and point your client's base URL at the router. Every run mode —
local, debug + `dotnet user-secrets`, Docker, GHCR image, Compose, and the Conductor fresh-sandbox — is
covered in **[`.docs/wiki/setup.md`](.docs/wiki/setup.md)** and the guides under
[`.docs/wiki/setups/`](.docs/wiki/setups/).

### Test

```bash
dotnet test SmoothLlmImposter.slnx
```

Tests are infra-free (no DB, no containers); integration tests stub the upstream transport in-process. See
[`.docs/wiki/testing.md`](.docs/wiki/testing.md).

---

## Tech Stack

| Component | Technology |
|---|---|
| Framework | ASP.NET Core minimal APIs (.NET 10) |
| Architecture | Clean Architecture — `Domain` / `Application` / `Infrastructure` / `Host` |
| Forwarder | `IHttpClientFactory` streaming proxy — no DB, no Mediator / FluentValidation pipeline (see [LADR-001](.docs/hld/001-llm-imposter-routing/ladrs/LADR-001-no-mediator-no-fluentvalidation.md), [LADR-002](.docs/hld/001-llm-imposter-routing/ladrs/LADR-002-stateless-no-ef-postgresql.md)) |
| Persistence | None for routing; PostgreSQL only for the optional credential-admin API |
| Logging | Serilog |
| Testing | xunit.v3 · Shouldly · Bogus |

This repo also ships an **AI-agent scaffold** under [`.agents/`](.agents/) — one source of truth for Claude
Code, GitHub Copilot, Cursor, and OpenAI Codex (skills, rules, hooks). After cloning, run
`bash .agents/setup/scripts/agents-setup.sh` once so the agents can discover it. See [`AGENTS.md`](AGENTS.md).

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

---

## Documentation

| Topic | Location |
|---|---|
| Setup & run (all modes) | [`.docs/wiki/setup.md`](.docs/wiki/setup.md) · [`.docs/wiki/setups/`](.docs/wiki/setups/) |
| Design (HLD, NFRs, LADRs) | [`.docs/hld/001-llm-imposter-routing/`](.docs/hld/001-llm-imposter-routing/README.md) |
| AI agent context & coding rules | [`AGENTS.md`](AGENTS.md) · [`.agents/`](.agents/) |
| AI tooling setup | [`.docs/wiki/ai-tooling.md`](.docs/wiki/ai-tooling.md) |
| Testing strategy | [`.docs/wiki/testing.md`](.docs/wiki/testing.md) |
| CI/CD pipeline | [`.docs/wiki/ci.md`](.docs/wiki/ci.md) |

---

## Contributing

- Work on a branch off `main`: `<type>/<ticket>-short-description` (e.g. `feat/15-add-dialect-prefix`).
- Commits and PR titles follow [Conventional Commits](https://www.conventionalcommits.org). See
  [`.agents/rules/git/`](.agents/rules/git/).
- Every PR should create or update at least one `*_AGENTS.md` context file.
