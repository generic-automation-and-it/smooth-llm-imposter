# LADR-02: Translate after planning, before the decision filter; sanitize headers and auth

**Status:** Accepted

## Context

- The existing `/responses`-to-Chat downgrade rewrites the effective upstream path to `/v1/chat/completions` after planning, and [HLD 014](../../014-decisions-tools-filter/README.md) already filters Chat bodies at that path.
- The forwarder relays caller headers minus its fixed non-forwardable and managed-auth set, and adds `anthropic-version` for Anthropic-dialect providers; for a third-party Chat host that relay would leak `cookie`, `user-agent`, `anthropic-beta` and SDK fingerprint headers.
- Anthropic-only prompt-cache breakpoints have no meaning on a Chat upstream.

## Decision

**Adopt** the downgrade's seam: plan and model rename first, then translate the request body and rewrite the effective path to the Chat endpoint, then run the decision filter on the Chat body, then forward. The Anthropic cache-breakpoint injection is skipped on bridged routes; tool-catalog ordering still applies because it runs earlier on the original body.

On a bridged route the outbound header set is an **allowlist** (content type, accept, the managed credential, and the session header the router already stamps); no caller header is relayed. The credential value follows the provider's `AuthScheme` (the router's `AuthHeader` rule relocates the header name only). The exact list is in the [harness spec](../spec/04-harness-and-errors.md).

## Consequences

- This LADR narrows the routing "transparent proxy — do not strip or rewrite the request" rule for bridged routes only: on a bridged route the caller's header set is replaced by the allowlist, an explicit exception recorded here (as [LADR-01](LADR-01-per-provider-opt-in.md) records the same-dialect narrowing). Passthrough, default and native `/v1/messages` routes stay byte-transparent with respect to the bridge (the independent HLD 014 decision filter still applies where enabled).
- The filter sees Chat-shaped tools and messages; its Chat handling must be verified against bridged Claude Code traffic.
- Nothing the caller sent (credential, cookies, client fingerprint) reaches a third-party Chat host.
- `--who?` and `--newsession` keep working because they run before the forward seam.
