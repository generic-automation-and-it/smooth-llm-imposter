# NFR-03 — Secret-adjacent logging

- **Category:** Security / Observability
- **Status:** Accepted · 2026-07-24

## Requirement

Raw session-identity values and fingerprint inputs must never appear in
any log sink. Fingerprint inputs are named in [README §2](../../README.md#2-stateless-identity-resolution-capture--derive--none)
(`chatgpt-account-id`, `openai-organization`, `openai-project`,
`Authorization`, `x-api-key`, body `user`) and enumerated in
`SessionIdentityResolver.FingerprintHeaderNames`.

## Acceptance Criteria

- Routing Information line includes `session=captured|derived|none` only.
- The `LogToken` switch is exhaustive: `UnreachableException` throws on
  a new `SessionIdentitySource` value.
- All four resolver capture headers (`session_id`, `x-opencode-session`,
  `x-session-id`, `conversation_id`) are in `SensitiveHeaderNames.Values`
  and masked in both inbound (Host) and outbound (Infrastructure) Debug
  dumps.

## Verification

- `SessionIdentity.LogToken` initializer is a switch with `_ => throw new
  UnreachableException()` — compile-time fail on new enum values.
- `tests/SmoothLlmImposter.Application.UnitTest/Routing/ImposterRouterTests.cs`
  covers the derived-path log triad.

## Applies To

- `SessionIdentity.LogToken`
- `SensitiveHeaderNames` (Host inbound + Infrastructure outbound)
- `ImposterRouter` Information log
