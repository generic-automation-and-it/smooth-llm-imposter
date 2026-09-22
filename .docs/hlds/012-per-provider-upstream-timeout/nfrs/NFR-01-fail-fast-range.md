# NFR-01: A configured timeout is validated at startup, never silently discarded

**Status:** Accepted

## Requirement

`Imposter:Providers:<key>:TimeoutSeconds`, when present, must be an integer in **1–3600**.
A value outside that range fails `ValidateOnStart` with a message naming the provider, the
field, and the accepted range. An unparseable *conventional env* value is ignored with a
`Warning` that names the variable, the provider, and the field — never the value's semantics.

## Rationale

The failure mode this guards against is the one HLD 011 shipped with: an option that is
present in config, readable through the admin API, and quietly inert. A timeout is worse than
most, because the symptom of a discarded value (requests failing at 300 s) is indistinguishable
from the symptom of an applied one that is simply too low.

`0` and negatives are rejected rather than clamped: a zero header timeout fails every attempt
instantly and the retry handler cannot tell that from a dead upstream. The upper bound of 3600 s
per attempt (6 hours worst case across the scaled ladder) is a typo guard, not a policy.

## Verification

- `ImposterOptionsValidatorTests` — 1 / 300 / 3600 accepted; 0 / -1 / 3601 fail with a message
  containing `TimeoutSeconds`.
- `ImposterOptionsPostConfigureTests` — the `_TIMEOUT_SECONDS` suffix applies to the field; a
  non-numeric value is ignored with a warning rather than applied.
- `DependencyInjectionTests` — a non-positive stamp that somehow reaches the pipeline resolves
  to the default base rather than a zero timeout.
