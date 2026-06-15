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
    config/env only, never persisted, and the inbound caller's credentials are never forwarded.
  - `.docs/hld/001-llm-imposter-routing/` HLD and `Features/Routing/ROUTING_AGENTS.md` feature context.

### Changed
- Renamed the template scaffold `Project.*` → `SmoothLlmImposter.*` (solution, projects, namespaces,
  folders) and removed the template notice from `AGENTS.md`.
- Split HLD 001 into a `README.md` index plus `diagrams/`, `nfrs/`, and `ladrs/` subfolders (one file
  per diagram, NFR, and LADR) instead of a single monolithic document.
- Removed redundant `.gitkeep` files from folders that now contain code (`Features/`, `Endpoints/`).
- Default `appsettings.json` providers reworked: `opencode-go` → `https://opencode.ai/zen/go` with
  `gpt5.4` → `kimi-k2.7`; added `openrouter` (openai) and `opencode-anthropic` (`claude-haiku-*` →
  `minimax-m3`). Removed the `IsDefault` passthrough providers — impostering is now type-only and an
  unmatched model returns a 404 (HLD LADR-005). The `IsDefault` capability remains supported in code.

### Removed
- EF Core / PostgreSQL and the Aspire/WireMock/Respawn test stack (the service is stateless). Integration
  tests now stub the upstream transport in-process — no containers or database required.
