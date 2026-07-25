# LADR-04: Default-ON opt-out config (shared by all customized switches)

**Status:** Accepted

## Context

The customized-switches feature family (`--who?` and `--newsession`, plus the
in-memory session-id translation dictionary consulted on the forward path) costs the
operator nothing at runtime — zero upstream calls, no new surface — and delivers
immediate value for agent clients diagnosing a misroute or wanting a stable
in-memory session id. The alternative — default-OFF, opt-in — creates an adoption
cliff: operators have to know the feature exists and add a config line before anyone
can use it, which for a near-zero-cost feature is the wrong default.

The whole feature family lives behind a single toggle. Per-switch toggles (e.g.
`Imposter:WhoMessage:Switches[0].Enabled`) would multiply the config surface for
no benefit when the typical use case is "all switches on" or "all switches off".

## Decision

**Default the feature family ON** via `Imposter:WhoMessage:Enabled: true`, with the
conventional env override `IMPOSTER_WHO_MESSAGE_ENABLED`. The boolean is parsed by
the root-level `ApplyRootBooleanOverride` helper (`ImposterOptionsPostConfigure.cs`),
which mirrors the bool.TryParse + warn-on-invalid semantics of the per-provider
`_IS_DEFAULT` / `_ENABLED` field loop: invalid values log a Warning and leave the
bound value unchanged (fail-safe toward the appsettings default, not toward a
crash).

The toggle is read via `IOptions<ImposterOptions>` at the endpoint seam, so its value
is captured at startup; flipping the env var **requires a host restart** to take
effect. This is consistent with every other `Imposter` option in the codebase (no
`IOptionsMonitor` usage for routing config today) and keeps the switches' config
surface uniform with its siblings.

When the toggle is `false`:
- The short-circuit responder is not invoked — no body parsing for trigger detection.
- The in-memory session-id translation dictionary is not consulted on the forward
  path — the captured/derived `SessionIdentity` passes through unchanged.
- The non-match forward path is byte-identical to the pre-HLD router (NFR-01).

## Alternatives Considered

- **Default-OFF, opt-in** — rejected: adoption cliff for a near-zero-cost feature.
- **Per-switch toggle** — rejected: the switches are a cross-cutting affordance, not
  per-route decisions; a per-switch toggle multiplies config surface for no benefit
  (a future per-switch opt-in can be added inside the switch table when justified).
- **Per-provider toggle** — rejected: the switches and the translation dictionary are
  proxy-wide, not per-provider; a per-provider toggle would interact badly with the
  forwarder-pass-through identity forwarding.
- **No config at all (always on)** — rejected: operators must be able to prove their
  forward path is byte-identical to a stock proxy when debugging unrelated issues; the
  toggle exists for that proof.

## Consequences

- Positive: the whole feature family works out-of-the-box on every install; no
  onboarding step.
- Positive: the toggle lets operators demonstrate byte-identical forward behavior when
  needed (e.g. during a production investigation) — both the switch short-circuit AND
  the translation dictionary are off together.
- Negative: callers who do not want the switches to be *possible* (e.g. a hardened
  gateway mode) have to flip a config; the default does not err toward locked-down.
- Neutral: the env override follows the established naming convention, so operators
  already familiar with `<PREFIX>_IS_DEFAULT` do not need a new mental model.
- Neutral: the toggle name `WhoMessage:Enabled` is the existing name; an operator
  reading the config sees a probe-related knob and may not realize it also gates the
  session-id translation. LADR-04 keeps the existing name; a future HLD may rename
  the node to `Imposter:Switches:Enabled` to match the broader feature family without
  breaking the env-var contract (the new env var would be `IMPOSTER_SWITCHES_ENABLED`,
  with `IMPOSTER_WHO_MESSAGE_ENABLED` retained as a deprecated alias).

## Related

- **LADR-01** — the seam is the consumer of the enabled flag.
- **LADR-02** — the switch table is consulted only when the toggle is `true`.
- **LADR-06** — the in-memory translation dictionary is consulted only when the
  toggle is `true`.
- **NFR-01** — the disabled state is the transparency proof: with `Enabled:false`,
  the forward path is byte-identical.
