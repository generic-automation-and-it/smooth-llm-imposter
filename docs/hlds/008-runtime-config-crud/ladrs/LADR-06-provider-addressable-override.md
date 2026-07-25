# LADR-06: Provider-Addressable Authorization-Override and Activation

**Status:** Accepted

## Context

HLD 003's passthrough authorization-override and HLD 002's credential activation are keyed by **dialect**:
`/routing/{dialect}/override-authorization` and "activate the credential for this dialect". With multiple
named providers per dialect (HLD 007) and provider-keyed credentials
([LADR-05](./LADR-05-settings-backed-provider-keyed-credentials.md)), a dialect-only key can no longer
express *which* provider's auth to force or activate. The operator asked for these admin surfaces to be
"provider usable" and aligned with the routing change.

## Decision

**Make** the authorization-override and credential activation **provider-addressable**, e.g.
`/routing/{dialect}/{provider}/override-authorization`, where `{provider}` is the stable provider dictionary key.
When a request names **only a dialect** (no
provider), it resolves to that dialect's **default** provider — preserving today's ergonomics for the common
single-provider case. The **inbound proxy URLs are unchanged**: provider addressing is confined to the admin
/ routing-control surface; the data-plane proxy still routes by dialect endpoint + model-mapping → default.

## Alternatives Considered

- **Keep dialect-only keying** — rejected: cannot disambiguate among multiple providers of the same dialect.
- **Add a provider segment to the inbound proxy URL too** — rejected by product decision: callers' SDKs hit
  fixed `/openai`/`/anthropic` paths; provider selection stays config-driven on the data plane.

## Consequences

- Operators can arm an override or activate a credential for a specific named provider.
- Dialect-only calls remain valid and target the default provider — backward-compatible for single-provider
  dialects.
- The override switch and activation state become keyed by `(dialect, provider)`; their in-memory structures
  and endpoint routes change accordingly.
- The fail-closed and imposter-parity guarantees from HLD 002/003 must be preserved per provider.

## Related

- **LADR-05** — the provider-keyed credentials this override/activation acts on.
- Amends HLD 003 (override) and the activation surface of HLD 002.
