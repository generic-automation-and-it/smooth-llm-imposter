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
- **Dump the full inbound request for debugging.** Default log level is `Information` (operation
  lines only). Set `Serilog:MinimumLevel:Override:SmoothLlmImposter.Routing` to `Debug` to log
  every routed call's HTTP method, upstream path, query string, **all headers**, and the **raw
  body** (auth secrets masked) — no rebuild, no client change. See the
  [debug logging guide](.docs/wiki/setups/logging.debug-smooth-llm-imposter.md).

### Why this exists — and how to combine it

Generic LLM gateways ([LiteLLM](https://github.com/BerriAI/litellm), [AWS Bedrock](https://aws.amazon.com/bedrock/),
[Azure AI Foundry](https://ai.azure.com/), [Vertex AI](https://cloud.google.com/vertex-ai),
[OpenRouter](https://openrouter.ai/), [Portkey](https://github.com/Portkey-AI/gateway), [Bifrost](https://github.com/maximhq/bifrost),
…) are **API-key gateways**: they front metered, pay-as-you-go upstreams and assume every
provider authenticates with a paid key. They have no first-class support for subscription tiers
(Claude Pro/Max, ChatGPT Plus, GitHub Copilot, `claude setup-token`). This router fills that gap:

- **Subscription-aware.** Forwards the caller's own subscription credential (Claude `setup-token`,
  ChatGPT session, etc.) or applies a configured subscription bearer per provider.
- **Model aliasing with cache injection in one step.** A mapping's `Caching` flag rewrites the
  body to add `cache_control` / `prompt_cache_key` markers the upstream wouldn't add — no
  callback code.
- **Stateless, key-less, single binary.** No DB, vault, admin UI, or Redis — just
  `appsettings.json` + env var.
- **Dual-dialect, same-dialect only.** OpenAI + Anthropic at the edge, no cross-dialect
  translation (subscription endpoints are dialect-locked).

Point any mapping's `BaseUrl` at another gateway (LiteLLM, Bedrock, Azure AI Foundry, Vertex AI,
OpenRouter, …) to layer subscription routing on top of whatever that gateway provides — cost
tracking, virtual keys, load balancing, regional failover. Or run the other gateway at the edge
and have one of its entries point back at SmoothLlmImposter for subscription-backed models.

**TL;DR — most gateways route API keys. This one routes subscriptions.** Pick the one that fits,
or stack them.

---

## How it works

Inbound dialect is chosen by the endpoint/prefix. The router reads `model`, selects the first matching
provider mapping (config order), rewrites the model, optionally injects caching, applies the provider's
configured `Secret`/`AuthScheme`, and streams the upstream response back unbuffered. An unmatched model
either passes through to the dialect's default provider (forwarding the caller's own credentials) or, when
no default is configured, returns a dialect-shaped 404.

Design detail — actors, request flow, routing decision, NFRs, and decision records — lives in the
HLDs under `.docs/hld/`:

| HLD | Status | Covers |
|---|---|---|
| [001 — LLM Imposter Routing](.docs/hld/001-llm-imposter-routing/README.md) | Accepted | Core routing — read `model`, match provider, rewrite, forward, optional cache injection |
| [002 — Credential Persistence & Overrides](.docs/hld/002-credential-persistence-overrides/README.md) | Accepted | Opt-in PostgreSQL persistence for passthrough-credential overrides; amends HLD 001 |
| [003 — Passthrough Authorization Override](.docs/hld/003-passthrough-authorization-override/README.md) | In Discovery | Force the active stored Bearer over the caller's credential on passthrough routes |

---

## Quick start

Pull the published image and pass your provider keys through from the shell (the `-e NAME` form
inherits the value from your environment, so keys never land in command history):

```bash
docker run -d --name smooth-llm-imposter --restart unless-stopped -p 5080:5080 \
  -e Imposter__Providers__2__Secret \
  -e Imposter__Providers__3__Secret \
  -e Imposter__Providers__4__Secret \
  ghcr.io/generic-automation-and-it/smooth-llm-imposter:latest

export Imposter__Providers__2__Secret="sk-your-opencode-key"     # provider 2 — opencode-go (ApiKey)
export Imposter__Providers__3__Secret="sk-your-openrouter-key"   # provider 3 — openrouter (Bearer)
export Imposter__Providers__4__Secret="sk-your-anthropic-key"    # provider 4 — opencode-anthropic (ApiKey)

curl -fsS http://localhost:5080/health
docker logs -f smooth-llm-imposter
```

For local build, every run mode, the `Imposter` config section, client base URLs, the
credential-admin API, and tests, see [`.docs/wiki/setup.md`](.docs/wiki/setup.md) (and the per-mode
guides under [`.docs/wiki/setups/`](.docs/wiki/setups/)) and
[`.docs/wiki/testing.md`](.docs/wiki/testing.md).

---

## Documentation

| Topic | Location |
|---|---|
| Setup & run (all modes) | [`.docs/wiki/setup.md`](.docs/wiki/setup.md) · [`.docs/wiki/setups/`](.docs/wiki/setups/) |
| Tech stack & project structure | [`.docs/wiki/architecture.md`](.docs/wiki/architecture.md) |
| Design (HLD, NFRs, LADRs) | [`.docs/hld/001-llm-imposter-routing/`](.docs/hld/001-llm-imposter-routing/README.md) — index table under [How it works](#how-it-works) |
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
