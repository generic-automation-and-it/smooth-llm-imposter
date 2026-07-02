# Changelog

All notable changes to SmoothLlmImposter are documented here.

## [Unreleased]

### Fixed
- **Blank conventional secret var no longer shadows a populated alias (or wipes an appsettings secret).**
  `ImposterOptionsPostConfigure` treated an empty-but-present env var as a real override, so a blank
  canonical `<NAME>_API_KEY` (e.g. compose `ANTHROPIC_API_KEY=${ANTHROPIC_API_KEY:-}` with the host var
  unset) claimed the first-present-wins `Secret` slot and shadowed a populated `<NAME>_AUTH_TOKEN` /
  `<NAME>_AUTHORIZATION_BEARER` alias — surfacing as `auth=none` on a matched imposter route. Empty/whitespace
  conventional values are now treated as absent (both for the first-present-wins ordering and the shared
  base-secret fallback), so the populated alias wins and an appsettings-bound `Secret` isn't blanked.
- **Conventional secret env vars now follow the auth scheme instead of a fixed `_API_KEY`-wins order.**
  When both a key and a token are exported (e.g. a personal `ANTHROPIC_API_KEY` alongside the gateway's
  `ANTHROPIC_AUTH_TOKEN`), the previous fixed precedence sent the api-key-typed `_API_KEY` even on a
  `Bearer` provider — so the wrong credential was presented and the upstream rejected it (403). The winning
  secret suffix now follows the provider's **effective** `AuthScheme` (naming-convention priority): `Bearer`
  prefers `_AUTH_TOKEN` → `_AUTHORIZATION_BEARER` → `_API_KEY`; `ApiKey` prefers `_API_KEY` → `_AUTH_TOKEN`
  → `_AUTHORIZATION_BEARER`. Off-scheme suffixes remain as fallbacks so a single populated var still
  authenticates. `ImposterOptionsPostConfigure` resolves the scheme (`_AUTH_SCHEME` env → bound `AuthScheme`
  → dialect default, via `UpstreamAuthResolver`) before choosing the secret, so the chosen var matches the
  header actually written. Applies to every provider.

### Added
- **LEGO gateway providers + `{model}` model-rewrite token.** `appsettings.json` gains two imposter
  providers pointing at the internal gateway: `anthropic` → `https://models.assistant.legogroup.io/claude`
  (rewrites `claude-opus-*`/`claude-sonnet-*`/`claude-haiku-*` to `anthropic.{model}`, `AuthScheme: Bearer`,
  credential from `ANTHROPIC_AUTH_TOKEN`/`ANTHROPIC_API_KEY`) and `openai` →
  `https://models.assistant.legogroup.io/openai` (pins `gpt-4o`, `gpt-5.4-*`, `gpt-5.5-*` to fixed upstream
  ids, credential from `OPENAI_API_KEY`). Both are placed first so first-match-wins routing makes the gateway
  authoritative for those families; `Secret` stays empty in config (supplied via env/user-secrets).
  - The model-mapping `To` field now supports the literal token **`{model}`** (`ModelMapping.ResolveTarget`),
    which expands to the full inbound model name — enabling prefix rewrites that keep the caller's version
    suffix (`To: "anthropic.{model}"` → `anthropic.claude-opus-4-1`). Literal `To` values are unchanged.
  - The conventional env surface gains a third Secret alias **`_AUTH_TOKEN`** (→ `Secret`), mirroring the
    Claude Code / Anthropic SDK `ANTHROPIC_AUTH_TOKEN` Bearer variable, so an operator can reuse the exact
    env var cc exports. One of three `Secret` suffixes alongside `_API_KEY` and `_AUTHORIZATION_BEARER`;
    which one wins follows the provider's auth scheme (see the scheme-driven priority under **Fixed**).
- **Personal-subscription providers (HLD 007 LADR-04).** Added two named providers to `appsettings.json` for
  the "company subscription for daily use, personal for private use" split: `anthropic-personal` captures
  `claude-opus-4-7*` and serves it as `claude-opus-4-8` on the operator's own Anthropic subscription token
  (`AuthScheme: Bearer`, committed `Secret` empty — supplied via env), and `openai-personal` is an inert
  codex passthrough template (no `Models` until the operator adds them). Neither is `IsDefault`.
  `openrouter-anthropic`'s glob narrowed to `claude-opus-4-6*` so the Opus globs stay distinct.
  - The conventional env surface gains an auth-typed secret alias **`_AUTHORIZATION_BEARER`** (→ `Secret`),
    so a Bearer subscription token reads as `ANTHROPIC_PERSONAL_AUTHORIZATION_BEARER` /
    `OPENAI_PERSONAL_AUTHORIZATION_BEARER`. A Bearer-typed alias of `Secret`; on a `Bearer` provider it is
    preferred over `_API_KEY` (see the scheme-driven priority under **Fixed**).
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
- **Docker build restore path.** Renamed the build-stage `WORKDIR` so the image still mirrors the repo-root
  `src/SmoothLlmImposter.*` layout while Docker restore/publish output no longer shows a doubled `src/src/`
  path.
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
- **Opt-in persistence with an in-memory default.** `AddInfrastructure` registers EF Core + the
  PostgreSQL-backed `CredentialStore` only when `ConnectionStrings:ImposterDb` is set; otherwise an in-memory
  `InMemoryCredentialStore` is registered so the stateless/key-less default boots with no database and
  credential CRUD/activation work without one (the passthrough seam resolves the active stored credential if
  one exists, otherwise forwards the caller's own auth).
- **HLD 008 Phase 2 — provider-keyed credential overrides.** Credentials are settings-backed and keyed by the
  stable provider dictionary key (not the display `Name`); each provider holds its own active credential. The
  no-DB default is the `InMemoryCredentialStore` above (the prior silent `NullCredentialStore` is removed); the
  encrypted EF/PostgreSQL backend stays opt-in. Authorization-override is provider-addressable at
  `/routing/{dialect}/{provider}/override-authorization` with a dialect-only → enabled-default-provider
  fallback; activation is per-credential at `PUT /admin/credentials/{id}/activate` (at most one active per
  `(dialect, providerName)`). The inbound proxy URL is unchanged. The imposter hot path remains store-free
  (HLD 001 / HLD 002 LADR-004 parity).

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
- Default `appsettings.json` `openrouter` provider now uses the `responses` upstream default (dropped
  `OpenAiUpstreamApi: chat_completions`): OpenRouter ships a native, OpenAI-compatible
  `/api/v1/responses` (beta), so the provider passes `/responses` through byte-transparently instead of
  downgrading to `/v1/chat/completions`. `opencode-go` keeps `chat_completions` (its zen surface has no
  `/responses`).

### Removed
- EF Core / PostgreSQL and the Aspire/WireMock/Respawn test stack (the service is stateless). Integration
  tests now stub the upstream transport in-process — no containers or database required.
