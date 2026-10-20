# GITHUB_COPILOT_AGENTS.md

## TL;DR

Opt-in GitHub Copilot upstream providers (Anthropic Messages + OpenAI Responses per prefix) with device login and local encrypted SQLite credentials; NativeAOT is the deployment baseline.

## Non-Negotiables

- **The feature flag is the exact env var `ENABLE_GITHUB_COPILOT`** — absent/blank means disabled, a malformed value fails startup, and the env mapping wins over `GithubCopilot__Enabled` (R6). Binding only `GithubCopilot__Enabled` silently misses the contract.
- **Tokens cross this contract as ciphertext only.** A plaintext access/refresh token never enters the store, a response, or a log (R9); the token columns hold ciphertext and `ApiOrigin` is the validated upstream origin.
- **The store is never read on the inference path.** Routing consumes a hydrated snapshot; the store is touched at startup, login/complete and refresh only (R4).

## Test References

- **L1** `SmoothLlmImposter.Infrastructure.ComponentTest` — real on-disk SQLite store round-trip/transaction/reopen; the bootstrap never deletes existing data.
- **L0** `SmoothLlmImposter.Application.UnitTest/Features/GithubCopilot` — options env mapping (R6/R10).

## Changelog

| Date | Change | Ref |
|:-----|:-------|:----|
| 2026-10-20 | Created: options + exact env flag contract, ciphertext-only connection contract, L1 persistence tests. Persistence is direct Microsoft.Data.Sqlite — the Dapper.AOT plan was spiked and rejected (no .NET 10 interceptors). | #126 |
