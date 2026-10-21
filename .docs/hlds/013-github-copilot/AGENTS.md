# AGENTS.md - GitHub Copilot Providers

## TL;DR

Read [README.md](./README.md) first — the design, its LADRs and NFRs live there. This feature is **implemented**;
the live own-OAuth-app probe is an external release gate.

## Non-Negotiables

- **`ENABLE_GITHUB_COPILOT` is the authoritative flag, and absent means off.** The mapping lives in
  `GithubCopilotOptionsPostConfigure` and throws on a malformed value; do not read `GithubCopilot__Enabled`
  directly for gating decisions. Endpoint/OpenAPI/Scalar mapping reads the resolved options in `Program.cs`.
- **Tokens are ciphertext outside a login/refresh call.** `IGithubCopilotConnectionStore` accepts ciphertext
  only; the isolated `IGithubCopilotSecretProtector` key ring lives under the feature data directory. Never log
  a token, device code or raw OAuth body — `message` fields interpolate only already-returned safe fields.
- **Routing reads the published snapshot, never SQLite or GitHub.** The store is touched at startup,
  login/complete and refresh only. A per-request store lookup is a design regression (NFR-002).
- **Reserved-key mutations go through `GithubCopilotProviderOwnership`.** Upsert/delete/enable must call it
  before touching the registry; the durable row, not a cache of usable tokens, is the ownership index. Hold the
  per-prefix gate across the row check, never the registry lock across awaited I/O (LADR-001).
- **Publish through `IGithubCopilotPublisher`.** It batch-applies the pair under one registry lock and rejects an
  invalid registry. Do not add a second write path that upserts the two keys separately (LADR-002).
- **Keep the Copilot path AOT-clean.** Persistence is parameterized `Microsoft.Data.Sqlite` ADO.NET with explicit
  row mapping; GitHub JSON uses source-generated metadata or `JsonDocument`. Dapper and EF Core are not valid on
  this path (LADR-004).
- **Never forward a Copilot credential off the same-dialect completion paths.** `UpstreamForwarder` applies
  `GithubCopilotTransportPolicy` — do not add a bypass that lets a managed token reach Chat or another origin.

## Key Behaviors

- Login flows are process-lifetime and bounded by the device-code expiry; a restart invalidates an unfinished
  login, while a saved revision is re-materialized at startup. A published flow stays answerable for a bounded
  window so a retried Complete returns the same connection ID/revision without another GitHub call.
- Publication is retried a bounded number of times inside Complete; retry exhaustion returns 503 with the saved
  revision and leaves the row intact. Startup hydration publishes every saved row or fails startup.
- An upstream 401 invalidates only the revision the request was planned against (`RoutePlan.Copilot`); a 403
  model-policy response does not revoke the account.
- Refresh-ahead is 10 minutes and runs outside inference; a rotated pair that cannot be saved fails closed
  rather than falling back to a possibly revoked token.
- The dedicated volume is removed only as an exact, whole-feature action; deleting a provider is not a logout.

## Test References

- **L0** `Domain.UnitTest/GithubCopilot` (profile parsing, transport policy),
  `Application.UnitTest/Features/GithubCopilot` (login state machine, refresh, startup hydration),
  `Application.UnitTest/ProviderConfiguration` (D1 reserved-key matrix).
- **L1** `Infrastructure.ComponentTest` — SQLite store round-trip/reopen/`List`, isolated key-ring decrypt.
- **L2** `Host.IntegrationTest/GithubCopilotIntegrationTests` — real Host + real SQLite/key ring, ordered
  workflow, both dialects, D1 refusals, revision-scoped 401, restart recovery, Chat-path refusal.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-10-20 | Initial HLD AGENTS.md — 4 LADRs, 2 NFRs; records the resolved-options gate, ciphertext-only store and published-snapshot inference path. | #126 |
