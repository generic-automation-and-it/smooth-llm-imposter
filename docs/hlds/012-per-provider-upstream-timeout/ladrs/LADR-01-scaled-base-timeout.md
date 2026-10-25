# LADR-01: One base timeout, scaled ×1/×2/×3 across the three attempts

**Status:** Accepted

## Context

The forwarder makes at most three attempts (initial + two retries on pre-response transport
failures). Each attempt has its own header timeout; before this HLD those were three
independent hardcoded values (200/600/900 s). Exposing "a timeout" per provider has to answer
which of the three the operator is setting.

## Decision

The provider configures a single **base** (`TimeoutSeconds`). Attempt *n* gets
`base × (n + 1)`, i.e. base, 2×base, 3×base. The default base is **300 s**, reproducing a
300/600/900 s ladder.

## Alternatives Considered

- **Flat: the base applies to all three attempts** — rejected. It discards the reason the
  ladder exists: a first failure is usually a transport hiccup worth retrying quickly, while a
  second one more often means a genuinely slow upstream that needs head-room. A flat 300 would
  cut the worst-case wait from 1800 s to 900 s for every provider, silently changing behaviour
  for anyone who set a value for an unrelated reason.
- **Configure all three values as a list** (`TimeoutSeconds: [300, 600, 900]`) — rejected. It
  cannot be expressed in the conventional single-valued env surface (HLD 007 LADR-02), which is
  how this option is actually set in deployment, and it exposes retry shape as a per-provider
  knob we do not want to support.
- **Override only the first attempt, keep 600/900 fixed** — rejected. An operator who raises
  the base to 900 for a slow local model would get 900/600/900: the retries would be *shorter*
  than the initial attempt, which is incoherent.

## Consequences

- Worst-case header wait for a provider is `6 × base` (1 + 2 + 3) plus the 1 s + 2 s retry
  delays. At the 3600 s validator cap that is 6 hours — deliberate: the cap exists to stop a
  typo, not to express a policy about sane upstreams.
- Changing the default base changes all three rungs at once, which is why the default moved as
  a single number (200 → 300) rather than as three edits.
