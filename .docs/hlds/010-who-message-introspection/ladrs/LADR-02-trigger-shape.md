# LADR-02: Exact-match `"--who?"` or `"--newsession"` on the last user message

**Status:** Accepted

## Context

The customized-switches feature family has to fire on the unambiguous intent of each
switch — *"tell me who I am routed to"* (`--who?`) or *"mint me a session id"*
(`--newsession`) — without false positives on ordinary conversation. Anything looser
than an exact match on the *last* user message either fires on regular user chatter
("Who is the president?", "who else is coming?") or on operator-controlled text the
caller never intended as a probe. The forwarder already keys streaming detection off
the body's `stream` field, so the trigger must be body-only to stay consistent with
the forward path.

The two-dash prefix (`--who?`, `--newsession`) is borrowed from the CLI-flag
convention. It is distinctive enough that a caller reading a transcript can spot the
probe intent at a glance and is unlikely to collide with natural English.

## Decision

**Adopt** (revised) a registered-switch table on the last `role:"user"` message: when
the trimmed, ordinal-equalled content matches any registered switch literal, the
responder short-circuits with the switch's reply shape. The live implementation
matches this LADR.

Two switches are registered:

- `--who?` → routing probe (LADR-03 envelope, content text per Goal 1)
- `--newsession` → session-id mint + translation (LADR-03 envelope, content text
  `Session: <callerId> → <syntheticId>`)

Both switches share the same predicate: last user message, exact match
(case-sensitive, ordinal), trimmed, non-streaming. Multi-part text content
(an array of text parts) matches when the concatenated trimmed value equals
the trigger literal. Bare-string-only narrowing is a planned follow-up.
The predicate is evaluated only when `stream != true` and the feature is enabled.

The match is ordinal (`StringComparison.Ordinal`) — no locale, no case-folding. The
switches are hardcoded constants (not configurable via `appsettings.json` or env)
but the responder's switch table is the single place to add a new switch in the
future (Goal 7).

## Alternatives Considered

- **Regex / case-insensitive / prefix match** — rejected: false positives on ordinary
  English text (`Who?` as an interjection, "who else…") and on operator-controlled
  prompts; adds parsing cost for no benefit.
- **Header-based trigger** (e.g. `X-Imposter-Probe: who`) — rejected: out-of-band,
  unusable from plain SDK chat clients, and collides with the existing header-relay
  contract.
- **Configurable trigger string** — rejected for the existing switches: adoption cliff;
  every deployment would need to align trigger with callers; operators who need a
  different trigger can fork the constant. **Reserved** as a future option (Goal 7:
  move the switch table to config) without churning the gate, the seam, or the
  translation dictionary.
- **Match any user message, not just the last** — rejected: would fire on conversation
  history carrying an earlier probe; last-message scoping keeps the trigger
  intentional per request.
- **Single prefix pattern (e.g. `--imposter-*`)** — rejected: the current pair of
  switches is short enough that a flat table is clearer than a wildcard; if the list
  grows past ~5, revisit.

## Consequences

- Positive: zero false-positive risk against realistic chat content; the trigger is
  greppable in a transcript (`--who?` / `--newsession`).
- Positive: the responder's predicate is a single `string.Equals` after trim per
  switch — no regex compile, no locale tables, no allocation beyond the concatenated
  content.
- Positive: easy to document — "send `--who?` or `--newsession` as your last message".
- Positive: extending the feature with a new switch is a localized change to the
  switch table — no change to the seam, the config gate, or the translation
  dictionary (Goal 7).
- Negative: case-sensitive means `Who?` / `WHO?` do not fire; accepted because the
  triggers are operator/agent affordances, not user-facing features.
- Negative: callers cannot use a synonym (`identify`, `route`); accepted because the
  value of the feature is operator/agent tooling, not user-facing UX.
- Negative: the switch table is a hardcoded set today; a future need for operator-
  configurable switches (e.g. per-deployment custom probes) would need the reserved
  config-driven switch table (Goal 7) — a one-time refactor when the third custom
  switch lands.
- Neutral: triggers do not change across streaming / non-streaming (LADR-05 controls
  the gate, not the shape).

## Related

- **LADR-01** — the seam provides the `requestBody` to inspect.
- **LADR-05** — streaming requests return no match (the trigger shape itself is unchanged).
- **LADR-06** — the in-memory translation dictionary consulted on the forward path
  when the resolved session id matches a key; the dictionary is populated by
  `--newsession` (Goal 6) and consulted on every non-match forward request that
  resolves a session id.
