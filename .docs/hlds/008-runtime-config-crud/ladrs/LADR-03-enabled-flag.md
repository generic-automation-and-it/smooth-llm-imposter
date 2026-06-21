# LADR-03: `Enabled` Flag per Provider

**Status:** Draft

## Context

Issue #48 calls for enabling/disabling a routing via the API. The current model has no such concept — a
provider is either present in configuration or not. Operators want to take a route out of service
temporarily and bring it back without re-entering its full configuration (URL, mappings, scheme, secret).

## Decision

**Add** an `Enabled` boolean to a provider (defaulting to enabled for backward compatibility). A
**disabled** provider is **excluded from routing resolution entirely** — it is skipped during imposter
model-matching and is never selected as a dialect default/passthrough provider, exactly as if it were absent
— but its configuration is **retained** in the registry. Re-enabling restores it to resolution unchanged.

## Alternatives Considered

- **Delete to disable, re-create to enable** — rejected: loses the configuration, defeating the "park and
  restore" use case and forcing secret re-entry.
- **A separate disabled-set rather than a flag on the provider** — rejected: splits a provider's state
  across two structures; the flag keeps one coherent record.

## Consequences

- Operators can park/restore a route with a single field toggle, surviving across other edits.
- Resolution must consult `Enabled` in both the imposter-match loop and the default-selection step.
- Disabling the **only** default for a dialect means dialect passthrough has no default and fails closed
  (404) until another default is enabled — consistent with today's "no default configured" behaviour.

## Open

- Whether disabling a provider that is the active credential target should also surface a warning — owner:
  design review on #48; trigger: credential-alignment phase.

## Related

- **LADR-01** — the flag lives on the runtime registry record.
- Interacts with HLD 001 default-passthrough resolution (LADR-005 there).
