# NFR-02 — Safe default transparency

- **Category:** Compatibility
- **Status:** Accepted · 2026-07-24

## Requirement

When `SessionForwarding` is unset on a route, the forwarded request
must be byte-equivalent to the inbound request on that route.

## Acceptance Criteria

- With `SessionForwarding = None` on a matched imposter route, the
  forwarded body bytes equal the inbound body bytes for the same
  request.
- With `SessionForwarding = None` on a passthrough route, the forwarded
  body and header bytes equal the inbound body and header bytes.

## Applies To

- `OpenAiRequestTransformer` (skip body stamp)
- `AnthropicRequestTransformer` (header-only path already byte-neutral)
- `UpstreamForwarder.ApplySessionIdentity` (early-return on opt-out)

## Verification

- L0 transformer tests + L2 integration passthrough assertion.
