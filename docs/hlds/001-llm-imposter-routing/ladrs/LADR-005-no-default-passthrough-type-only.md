# LADR-005 — Type-only impostering, no default passthrough configured

- **Date / Status:** 2026-06-15 · Accepted

## Context

Earlier the shipped config declared an `IsDefault` provider per dialect so unmatched models would
pass through to the real provider. In the target deployment the **calling client already points its
SDK at the real provider's base URL** for any call it does not want impostered — it only sends
traffic to SmoothLlmImposter for models it wants redirected. A default passthrough therefore adds a
second place to keep upstream URLs/keys and invites accidental forwarding of unintended traffic.

## Decision

Ship **no default providers**. SmoothLlmImposter imposters **only on configured model types**; a
request whose model matches no mapping returns a dialect-shaped **404**. The `IsDefault` capability
remains supported in code (`RouteResolver` + validator) for deployments that want it, but the default
`appsettings.json` declares none.

## Consequences

- Unmatched models → 404 instead of silent passthrough; clients must target the real provider
  directly for non-impostered calls.
- No upstream base URL/key needs to be configured for "passthrough" — fewer secrets at rest.
- Re-enabling passthrough is a pure config change (add a provider with `"IsDefault": true`); no code
  change required.
