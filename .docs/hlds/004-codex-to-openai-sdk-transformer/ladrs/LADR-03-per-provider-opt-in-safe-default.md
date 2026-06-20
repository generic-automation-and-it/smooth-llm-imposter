# LADR-03: Per-provider opt-in, off by default

**Status:** Prototype

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

Normalization is only ever correct for a specific strict upstream; applying it to an upstream that
accepts the original shape would silently alter requests for no reason. The router already selects
per-upstream behavior through provider configuration (e.g. `OpenAiUpstreamApi`), validated at
startup. Normalization needs the same kind of explicit, per-provider control.

## Decision

**Gate** normalization behind a **per-provider opt-in that is off by default**. A provider that does
not opt in forwards requests byte-identically to today. The opt-in lives in provider configuration
and is validated at startup alongside the other provider options, so a misconfiguration fails fast
rather than silently mis-normalizing live traffic.

The concrete configuration shape (a flag, an enum, or a named set) is a tactical detail left to
implementation; this decision fixes only that the control is per-provider, defaulted off, and
startup-validated.

## Alternatives Considered

- **Global on/off switch** — rejected: normalization correctness is per-upstream; a global toggle
  would mis-normalize providers that don't need it.
- **On by default** — rejected: violates safe-default; would change current behavior for every
  existing provider on upgrade.

## Consequences

- Existing providers are unaffected until explicitly opted in (safe upgrade).
- One more provider config field to document and validate.
- Different imposter providers can enable different normalization profiles independently.

## Related

- **LADR-01** — the seam this opt-in gates.
- **NFR-02** — the safe-default guarantee this decision underwrites.
