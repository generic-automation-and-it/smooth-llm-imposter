# LADR-04: Default-ON opt-out config

**Status:** Accepted

## Context

The probe is a debugging affordance that costs the operator nothing (zero upstream
calls, no new surface) and delivers immediate value for agent clients diagnosing a
misroute. The alternative — default-OFF, opt-in — creates an adoption cliff: operators
have to know the feature exists and add a config line before anyone can use it, which
for a near-zero-cost feature is the wrong default.

## Decision

**Default the feature ON** via `Imposter:WhoMessage:Enabled: true`, with the conventional
env override `IMPOSTER_WHO_MESSAGE_ENABLED`. The boolean is parsed using the existing
`_IS_DEFAULT` post-configure pattern: invalid values log a Warning and leave the bound
value unchanged (fail-safe toward the appsettings default, not toward a crash).

Operators who want the forward path fully byte-identical to a pre-HLD router set
`Enabled: false` (or `IMPOSTER_WHO_MESSAGE_ENABLED=false`) and the short-circuit seam
is skipped entirely — no body parsing, no trigger check, no responder invocation.

## Alternatives Considered

- **Default-OFF, opt-in** — rejected: adoption cliff for a near-zero-cost feature.
- **Per-provider toggle** — rejected: the probe is a cross-cutting debugging affordance,
  not a routing decision; a per-provider toggle multiplies config surface for no
  benefit.
- **No config at all (always on)** — rejected: operators must be able to prove their
  forward path is byte-identical to a stock proxy when debugging unrelated issues; the
  toggle exists for that proof.

## Consequences

- Positive: the feature works out-of-the-box on every install; no onboarding step.
- Positive: the toggle lets operators demonstrate byte-identical forward behavior when
  needed (e.g. during a production investigation).
- Negative: callers who do not want the probe to be *possible* (e.g. a hardened gateway
  mode) have to flip a config; the default does not err toward locked-down.
- Neutral: the env override follows the established naming convention, so operators
  already familiar with `<PREFIX>_IS_DEFAULT` do not need a new mental model.

## Related

- **LADR-01** — the seam is the consumer of the enabled flag.
- **NFR-01** — the disabled state is the transparency proof: with `Enabled:false`, the
  forward path is byte-identical.
