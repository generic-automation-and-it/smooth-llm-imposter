# LADR-01: Per-provider opt-in on matched imposter routes

**Status:** Accepted

## Context

- [AGENTS.md](../../../../src/SmoothLlmImposter.Application/Features/Routing/AGENTS.md) states "same-dialect only"; this HLD narrows that rule, it does not remove it.
- Inbound model matching and route resolution are already per dialect, and a provider already declares which upstream API it speaks (`OpenAiUpstreamApi`).
- Passthrough and Anthropic-native providers are production traffic and must not change.

## Decision

**Adopt** a provider option `AnthropicUpstreamApi` with values `messages` (default) and `chat_completions`. The provider stays an Anthropic-dialect provider: inbound `/v1/messages` models still match it, and only its upstream wire changes. The bridge runs only on a **matched imposter route** of such a provider; passthrough/default routes never translate.

The option follows the plumbing every provider option needs (options model, env suffix, cloner, both admin DTOs, validator). A `chat_completions` provider is rejected at startup/upsert when its effective auth scheme (the provider's `AuthScheme`, else the Anthropic default `ApiKey`) cannot produce a managed credential, because a bridged route needs a router-managed credential of a form the upstream accepts.

## Consequences

- Operators opt in per provider; one router can serve bridged and native Claude traffic side by side.
- The "same-dialect only" rule in `AGENTS.md` must be amended when this HLD is accepted.
- Reserved GitHub Copilot providers stay fixed to their native API ([HLD 013](../../013-github-copilot/README.md)).
