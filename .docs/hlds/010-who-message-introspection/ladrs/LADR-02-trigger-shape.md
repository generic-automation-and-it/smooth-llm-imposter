# LADR-02: Exact-match `"who?"` on the last user message

**Status:** Accepted

## Context

The probe has to fire on the unambiguous intent *"tell me who I am routed to"* without
false positives on ordinary conversation. Anything looser than an exact match on the
*last* user message either fires on regular user chatter ("Who is the president?",
"who else is coming?") or on operator-controlled text the caller never intended as a
probe. The forwarder already keys streaming detection off the body's `stream` field,
so the trigger must be body-only to stay consistent with the forward path.

## Decision

**Adopt** exact `who?` (trimmed, case-sensitive, ordinal) on the last `role:"user"`
message, evaluated only when `stream != true` and the feature is enabled. Content is
supported as either a bare string or an array of text parts (concatenated before
compare); any non-text content part in the last user message disables the trigger.
The match is ordinal (`StringComparison.Ordinal`) — no locale, no case-folding.

The trigger is hardcoded. It is not configurable via `appsettings.json` or env.

## Alternatives Considered

- **Regex / case-insensitive / prefix** — rejected: false positives on ordinary
  English text (`Who?` as an interjection, "who else…") and on operator-controlled
  prompts; adds parsing cost for no benefit.
- **Header-based trigger** (e.g. `X-Imposter-Probe: who`) — rejected: out-of-band,
  unusable from plain SDK chat clients, and collides with the existing header-relay
  contract.
- **Configurable trigger string** — rejected: adoption cliff; every deployment would
  need to align trigger with callers; operators who need a different trigger can
  fork the constant.
- **Match any user message, not just the last** — rejected: would fire on conversation
  history carrying an earlier `who?`; last-message scoping keeps the trigger
  intentional per request.

## Consequences

- Positive: zero false-positive risk against realistic chat content; the trigger is
  greppable in a transcript (`who?`).
- Positive: the responder's predicate is a single `string.Equals` after trim — no
  regex compile, no locale tables, no allocation beyond the concatenated content.
- Positive: easy to document — "send `who?` as your last message".
- Negative: case-sensitive means `Who?` / `WHO?` do not fire; accepted because the
  trigger is an operator probe, not a user-facing feature.
- Negative: callers cannot use a synonym (`identify`, `route`); accepted because the
  value of the feature is operator-diagnosis, not user-facing UX.
- Neutral: trigger does not change across streaming / non-streaming (LADR-05 controls
  the gate, not the shape).

## Related

- **LADR-01** — the seam provides the `requestBody` to inspect.
- **LADR-05** — streaming requests return no match (the trigger shape itself is unchanged).
