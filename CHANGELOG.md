# Changelog

All notable changes to SmoothLlmImposter are documented here.

## [Unreleased]

### Added
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
