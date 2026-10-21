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
- `UpstreamForwarder` applies the Copilot path/origin policy before attaching the token; the Host invalidates a connection revision on upstream 401 only.

## Test References

- **L0** `Domain.UnitTest/GithubCopilot` — suffix parsing and origin/path policy. `Application.UnitTest/Features/GithubCopilot` — login state machine, refresh, startup hydration. `Application.UnitTest/ProviderConfiguration` — D1 reserved-key matrix.
- **L1** `SmoothLlmImposter.Infrastructure.ComponentTest` — real on-disk SQLite round-trip/transaction/reopen/`List`, isolated key-ring round-trip and reopen.
- **L2** `SmoothLlmImposter.Host.IntegrationTest/GithubCopilotIntegrationTests` — real Host + real SQLite/key ring, ordered workflow, both dialects, D1 refusals, 401 invalidation, restart recovery.

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-20 | Implemented workflow: login/complete/list/get/models slices, publisher, startup hydration, refresh, D1 ownership in provider CRUD, forwarder path/header policy, Scalar/OpenAPI. | #126 |
| 2026-10-20 | Created: options + exact env flag contract, ciphertext-only connection contract, L1 persistence tests. Persistence is direct Microsoft.Data.Sqlite — the Dapper.AOT plan was spiked and rejected (no .NET 10 interceptors). | #126 |
