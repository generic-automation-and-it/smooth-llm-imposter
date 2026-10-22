# GITHUB_COPILOT_AGENTS.md

## TL;DR

Opt-in GitHub Copilot upstream providers (Anthropic Messages + OpenAI Responses per prefix) with device login and local encrypted SQLite credentials; routing reads a published in-memory snapshot, never the store.

## Non-Negotiables

- **The feature flag is the exact env var `ENABLE_GITHUB_COPILOT`** — absent/blank means disabled, a malformed value fails startup, and the env mapping wins over `GithubCopilot__Enabled` (R6). Binding only `GithubCopilot__Enabled` silently misses the contract. The Host gates endpoint/OpenAPI/Scalar mapping on the **resolved options** in `Program.cs`, not on a raw env read.
- **Tokens cross this contract as ciphertext only.** A plaintext access/refresh token never enters the store, a response, or a log (R9); the token columns hold ciphertext, `ApiOrigin` is the validated upstream origin, and only `IGithubCopilotSecretProtector` (its own key ring under the data directory) sees plaintext.
- **The store is never read on the inference path.** Routing consumes the published snapshot (`IGithubCopilotConnectionRegistry`); the store is touched at startup, login/complete and refresh only (R4). Admin GETs read durable metadata but never write.
- **`message` never interpolates a secret.** Build help text only from already-returned safe fields (`verificationUri`, `userCode`, login/connection IDs, provider keys, `Retry-After`). Never the GitHub `device_code`, a token, a credential, or a raw OAuth body.

## Key Behaviors

- One pending login per prefix; a second Start returns 409 until it completes or expires. A published flow stays answerable briefly so a retried Complete returns the same connection ID/revision without a GitHub call.
- A consumed device grant is never re-exchanged: the issued pair is retained in the flow across retryable identity, save and publication failures.
- `GithubCopilotPublisher` rewrites only the managed identity (origin, Bearer scheme, `AuthHeader:null`) of an existing reserved key and preserves `Models`, `Enabled`, `Name`, order, timeout and session settings; it validates and applies the pair as one registry batch.
- Reserved provider deletion is refused while a durable row exists (expired included) under the per-prefix gate; an orphan key with no row is removable.
- Two different `X-GitHub-Api-Version` values are in play and must not be unified: `GithubOAuthClient` sends the GitHub **REST** version (`2022-11-28`) to `api.github.com`, which answers an unknown version with `400`; `GithubCopilotTransportPolicy` sends the **Copilot API** version (`2026-08-01`, opencode's) to `*.githubcopilot.com`.
- `UpstreamForwarder` applies the Copilot path/origin policy before attaching the token; the Host invalidates a connection revision on upstream 401 only.
- No `Copilot-Integration-Id` is sent (inference or discovery), and a caller-relayed one is stripped. GitHub derives the integrator — and the model list it may serve — from the OAuth client ID, as opencode relies on; switching integrator means a new login with a different `clientId`.
- Inference headers mirror opencode's Copilot provider (`GithubCopilotRequestHints`, caller values of the same names dropped first): `Openai-Intent: conversation-edits` on both dialects; `X-Interaction-Type` from the agent's request class — Claude Code `x-claude-code-request-class` (only sent with `CLAUDE_CODE_GATEWAY_HINT_HEADERS` set) or `x-claude-code-agent-id`, Codex `x-openai-subagent` — else `conversation-agent`; `X-Interaction-Id` from `X-Claude-Code-Session-Id` / `session-id` / `session_id` (then `x-session-id` / `x-opencode-session` / `conversation_id`), omitted when none; `X-Initiator: agent` for a non-main interaction or a tool-loop body (last message not a user turn with non-`tool_result` content), else `user`; `Copilot-Vision-Request: true` when the body carries `image` / `input_image` parts (including inside tool results). Neither agent sends an initiator or vision hint, so both are body-derived.
- `anthropic-beta` gains `interleaved-thinking-2025-05-14` but keeps the caller's flags — opencode replaces the header, but Claude Code sends body fields (e.g. `context_management`) whose beta must survive. User-Agent stays `SmoothLlmImposter/1.0`; model discovery sends only auth, API version and User-Agent.

## Test References

- **L0** `Domain.UnitTest/GithubCopilot` — suffix parsing and origin/path policy. `Infrastructure.UnitTest/Routing/GithubCopilotRequestHintsTests` — interaction type/id, initiator and vision derivation. `Application.UnitTest/Features/GithubCopilot` — login state machine, refresh, startup hydration. `Application.UnitTest/ProviderConfiguration` — D1 reserved-key matrix.
- **L1** `SmoothLlmImposter.Infrastructure.ComponentTest` — real on-disk SQLite round-trip/transaction/reopen/`List`, isolated key-ring round-trip and reopen.
- **L2** `SmoothLlmImposter.Host.IntegrationTest/GithubCopilotIntegrationTests` — real Host + real SQLite/key ring, ordered workflow, both dialects, D1 refusals, 401 invalidation, restart recovery.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-22 | Aligned Copilot inference headers with opencode (User-Agent and OAuth client ID excepted): API version `2026-08-01`, `Openai-Intent` on both dialects, request-class-derived `X-Interaction-Type`, session-derived `X-Interaction-Id`, body-derived `X-Initiator` and `Copilot-Vision-Request`, merged `interleaved-thinking` beta. `X-Initiator` was always `user`, so every tool-loop turn presented as a user turn. L0 `GithubCopilotRequestHintsTests`, L2 header assertions. | — |
| 2026-10-22 | Dropped `Copilot-Integration-Id: smooth-llm-imposter` from inference and model discovery (caller values stripped). With an `Iv1.` client the account got `400 model not available for integrator "copilot-language-server"`; opencode sends no such header. L0/L2 tests pin its absence. | — |
| 2026-10-21 | Fixed: Complete looped on `github_unavailable` after an approved grant — `GithubOAuthClient` sent REST API version `2025-04-01` (a Copilot API version), and `api.github.com` rejected `/user` with `400`. Now `2022-11-28`; L0 tests pin the header on `/user` and `copilot_internal/user`. | — |
| 2026-10-21 | List help points to discovery before readiness verification; discovery step 04 | — |
| 2026-10-20 | Implemented workflow: login/complete/list/get/models slices, publisher, startup hydration, refresh, D1 ownership in provider CRUD, forwarder path/header policy, Scalar/OpenAPI. | #126 |
| 2026-10-20 | Created: options + exact env flag contract, ciphertext-only connection contract, L1 persistence tests. Persistence is direct Microsoft.Data.Sqlite — the Dapper.AOT plan was spiked and rejected (no .NET 10 interceptors). | #126 |
