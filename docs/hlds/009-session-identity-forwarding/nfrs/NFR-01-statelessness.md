# NFR-01 — Statelessness

- **Category:** Scalability
- **Status:** Accepted · 2026-07-24

## Requirement

SmoothLlmImposter must not persist any per-caller state to satisfy
session identity forwarding. All derivation must be a pure function of
the inbound request and configured options.

## Acceptance Criteria

- No new fields on any storage type (database, file, in-memory cache).
- `SessionIdentityResolver.Resolve` is referentially transparent: same
  inputs ⇒ same output, across process restarts.

## Applies To

- `SessionIdentityResolver`
- `ImposterRouter`
- `OpenAiRequestTransformer`
- `AnthropicRequestTransformer`
- `UpstreamForwarder`

## Verification

- Code review + unit tests; no new storage types/tables.
