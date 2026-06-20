# Changelog

All notable changes to SmoothLlmImposter are documented here.

## [Unreleased]

### Added
- **Codex → OpenAI-SDK request normalization (HLD 004).** New per-provider `RequestNormalization` config
  (`codex_to_openai_sdk` / `none`) adds a proxy-side, **request-only** normalization seam on matched OpenAI
  imposter routes so vanilla Codex clients work against strict OpenAI-compatible upstreams (e.g.
  `opencode-go`/kimi). v1 keeps only upstream-valid `function` tools: it drops unsupported tool `type`s
  (`custom`, `web_search`, `image_generation`, `tool_search`, …), **flattens** `namespace` wrappers into
  their nested function tools (preserving the Codex GitHub connector's tools), drops function names that
  fail `^[A-Za-z_][A-Za-z0-9_-]*$`, and cleans any `tool_choice` that referenced a removed tool. The
  response stream is never touched.
  - **ON by default for `OpenAiUpstreamApi: chat_completions`** (set `RequestNormalization: none` to opt
    out); a `responses` upstream keeps it off and the startup validator rejects an explicit
    `codex_to_openai_sdk` outside `chat_completions`/`openai`. Rationale: the reject rules are the *generic*
    OpenAI Chat Completions tool contract (openrouter, Bedrock, … 400 on the same Responses-dialect catalog),
    and normalization is a no-op for clean clients — so it is the correct default for chat upstreams. This
    **amends HLD 004 LADR-03** (originally per-provider opt-in, off by default). `responses` upstreams and the
    `anthropic` dialect stay byte-transparent.
- **L3 live-upstream eval tier (HLD 004 LADR-04 / NFR-04).** New
  `tests/SmoothLlmImposter.Upstream.EvalTest` project (excluded from `SmoothLlmImposter.slnx`) replays
  the tool-validation matrix against the real `opencode-go` upstream: it proves a raw Codex catalog run
  through the normalizer is accepted (200) and that an un-normalized catalog is still rejected (400).
  Run only by the new secret-gated `.github/workflows/pr-evals-gate.yml` (org `OPENCODE_API_KEY`),
  **neutral (skipped) when the secret is absent** and **non-blocking** initially. `.docs/wiki/testing.md`
  now defines the L3 tier.

### Fixed
- **Codex `/responses` → Chat Completions 400 ("tokenization failed") on `opencode-go`.** The
  Responses→Chat conversion now folds `role:"developer"` → `role:"system"`: Moonshot/kimi (and some
  OpenAI-compatible Chat upstreams) reject the OpenAI `developer` role, which Codex sends in its `input`.
  This is separate from tool normalization — together they were the two causes of the #19 400. Real
  `/responses` upstreams keep `developer` (the conversion runs only for `chat_completions`). The L3 eval
  case now reproduces the full failure (unsupported tool types + dotted name + developer role) live.
- **README — "Why this exists" comparison section.** New sub-section under
  [README → Use cases](README.md#use-cases) explains how SmoothLlmImposter differs from generic
  LLM gateways (LiteLLM, AWS Bedrock, Azure AI Foundry, Vertex AI, OpenRouter, Portkey, Bifrost),
  why they can't replace it (API-key gateways, no subscription-tier support, no built-in
  prompt-cache injection per mapping), and how to compose it with any of them by pointing a
  mapping's `BaseUrl` at the other gateway. Closing line: *most gateways route API keys. This
  one routes subscriptions.*
- **README — debug-logging use case.** New bullet under
  [README → Use cases](README.md#use-cases) notes that flipping the
  `SmoothLlmImposter.Routing` Serilog category to `Debug` (default `Information`) dumps the full
  inbound request (method, path, query, headers, raw body — auth masked) for every routed call,
  with a link to the
  [debug logging setup guide](.docs/wiki/setups/logging.debug-smooth-llm-imposter.md).
- **README — HLD table under How it works.** Replaced the single-link line with a 3-row table
  indexing the HLDs in `.docs/hld/` (001 Accepted, 002 Accepted, 003 In Discovery) with a
  one-line scope for each, so the README is the human-facing index for the HLD folder.
- **AGENTS.md — HLD table maintenance note.** New paragraph under
  [AGENTS.md → Architecture Decisions (NFRs)](AGENTS.md#architecture-decisions-nfrs) declares the
  README HLD table the canonical human-facing index and obliges AI agents / contributors to
  update it in the same PR when an HLD is created, removed, or changes status.
- **README — collapse Tech Stack & Project Structure into a Documentation pointer.** Removed
  the standalone **Tech Stack** and **Project Structure** sections from the root README (the
  content was a near-duplicate of AGENTS.md / `project-overview.instructions.md`); the existing
  **Documentation** table now points readers to the canonical home at
  [`.docs/wiki/architecture.md`](.docs/wiki/architecture.md) for tech stack and project
  structure.
- **`.docs/wiki/architecture.md` — populated.** The previously empty placeholder now hosts the
  Tech Stack table, the Project Structure tree, and a per-layer pointer table to each project's
  `*_AGENTS.md` context file (this is the new canonical home for that content).
- **README — collapse Getting Started into a Quick start pointer.** Removed the `### Prerequisites`,
  `### Build & run`, and `### Test` subsections — all three duplicate content already in
  [`.docs/wiki/setup.md`](.docs/wiki/setup.md) and
  [`.docs/wiki/testing.md`](.docs/wiki/testing.md). Replaced with a single-command `## Quick start`
  plus a one-paragraph pointer to the wiki for the full setup, all run modes, and tests.
- **README — Quick start now runs the published GHCR image.** Replaced the
  `dotnet run` one-liner with the three-block GHCR flow (run published image with
  pass-through `-e NAME` env vars → `export` the three provider secrets → `curl` health + tail
  logs), matching the pattern in
  [`.docs/wiki/setups/ghcr.run-smooth-llm-imposter.md`](.docs/wiki/setups/ghcr.run-smooth-llm-imposter.md).
  Local `dotnet run` and all other run modes remain one click away in the wiki pointer below.
- **LLM imposter routing service.** Stateless, key-less router exposing OpenAI (`/v1/chat/completions`,
  `/v1/responses`) and Anthropic (`/v1/messages`) dialect endpoints.
  - Config-driven, provider-centric routing: an array of providers, each with a base URL, key,
    provider name, dialect, and nested `{ From, To, Caching }` model mappings. Input model selects the
    imposter (exact or trailing-`*` wildcard); unmatched models pass through to the dialect's default provider.
  - Per-dialect prompt-cache injection when a mapping opts in (Anthropic `cache_control` ephemeral markers;
    OpenAI `prompt_cache_key`).
  - `appsettings.json` overridden by environment variables (env wins); startup options validation via
    `ValidateOnStart`.
  - SSE streamed end-to-end (unbuffered); dialect-shaped error envelopes; provider keys sourced from
    config/env only and never persisted. Caller credentials are forwarded only on the key-less passthrough
    path (see below) — never on matched imposter routes.
  - `.docs/hld/001-llm-imposter-routing/` HLD and `Features/Routing/ROUTING_AGENTS.md` feature context.
- **Transparent header forwarding + key-less catch-all passthrough.** The forwarder now proxies the caller's
  full inbound header set (`CallerHeaders`) to the upstream verbatim, minus a fixed hop-by-hop/content/auth
  set — so `anthropic-beta` (and beta body fields like `context_management`), vendor `x-*` headers, and the
  caller's own `anthropic-version` reach the provider instead of being dropped. The only managed header is
  auth: an unmatched model routed to a key-less `IsDefault` provider forwards the caller's own
  `Authorization`/`x-api-key` (so the shipped `api.anthropic.com` / `api.openai.com` defaults authenticate with
  the caller's credential), imposter routes use the provider's configured key, and the HLD-003 override forces
  the active stored Bearer.
- **Opt-in persistence.** `AddInfrastructure` registers EF Core + the PostgreSQL-backed `CredentialStore`
  only when `ConnectionStrings:ImposterDb` is set; otherwise a `NullCredentialStore` is registered so the
  stateless/key-less default boots with no database (the passthrough seam resolves a `null` credential).

### Changed
- Renamed the template scaffold `Project.*` → `SmoothLlmImposter.*` (solution, projects, namespaces,
  folders) and removed the template notice from `AGENTS.md`.
- Split HLD 001 into a `README.md` index plus `diagrams/`, `nfrs/`, and `ladrs/` subfolders (one file
  per diagram, NFR, and LADR) instead of a single monolithic document.
- Removed redundant `.gitkeep` files from folders that now contain code (`Features/`, `Endpoints/`).
- Default `appsettings.json` providers reworked: `opencode-go` → `https://opencode.ai/zen/go` with
  `gpt5.4` → `kimi-k2.7`; added `openrouter` (openai) and `opencode-anthropic` (`claude-haiku-*` →
  `minimax-m3`). Catch-all `IsDefault` passthrough providers for `anthropic` (`https://api.anthropic.com`)
  and `openai` (`https://api.openai.com`) lead the array (no key — they forward the caller's credential), so
  unmatched models pass through to the real provider. Remove them for type-only impostering (unmatched → 404).
- Fixed an EF Core model-build crash on the passthrough credential lookup: the TPH discriminator shadow
  column collided with the ignored CLR `ProviderDialect` property (NRE in `HasDiscriminator`). Renamed the
  discriminator/column to `Dialect` in the configuration, store queries, and the initial migration.
- **Breaking config:** renamed the provider wire-dialect key `Api` → `Dialect` (e.g.
  `Imposter:Providers:0:Dialect`, env `Imposter__Providers__0__Dialect`) to match the `ApiDialect`
  domain vocabulary. Existing configs using `Api` must be updated — the old key is no longer bound.

### Removed
- EF Core / PostgreSQL and the Aspire/WireMock/Respawn test stack (the service is stateless). Integration
  tests now stub the upstream transport in-process — no containers or database required.
