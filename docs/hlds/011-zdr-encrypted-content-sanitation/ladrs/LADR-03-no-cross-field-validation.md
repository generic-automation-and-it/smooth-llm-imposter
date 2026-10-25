# LADR-03: No cross-field validator rule; bad env values follow the existing bool log-and-skip semantics

**Status:** Accepted

## Context

`ImposterOptionsValidator` runs at startup under `ValidateOnStart` and fails the boot for
malformed provider config: an unparseable `OpenAiUpstreamApi`, an unparseable
`SessionForwarding`, and one cross-field rule (`RequestNormalization: codex_to_openai_sdk`
requires `OpenAiUpstreamApi: chat_completions`).

`StripEncryptedContent` is a `bool?`, so unlike those `string?` enum-ish fields it has no
invalid *bound* state — the configuration binder either produces a bool or fails. The open
questions were whether to add a cross-field rule, and what a malformed **env** value
should do.

## Decision

**No validator rule is added for this field.**

1. **No cross-field constraint exists to express.** By LADR-02 the option is valid in every
   combination of `OpenAiUpstreamApi` and `RequestNormalization`; that orthogonality is the
   design, so a rule would contradict it. It is also harmless on the Anthropic dialect,
   where the OpenAI transformer never runs.
2. **Malformed env values follow the existing per-provider bool path.** In
   `ImposterOptionsPostConfigure`, `<PROVIDER>_STRIP_ENCRYPTED_CONTENT` is parsed with
   `bool.TryParse` alongside `_IS_DEFAULT` and `_ENABLED`; an unparseable value is logged
   and the bound value is left unchanged. It does not fail the boot.

The consequence is deliberate and worth stating plainly: a typo'd
`…_STRIP_ENCRYPTED_CONTENT=yes` leaves the flag off and the upstream 400 unfixed, with only
a log line to explain it.

## Alternatives Considered

- **Fail boot on an unparseable env value** — rejected for this field alone, because it
  would make ZDR sanitation stricter than `_IS_DEFAULT` and `_ENABLED`, which govern
  whether a provider routes at all. If that strictness is wanted it belongs as one change
  across all three bools, in its own decision.
- **Warn when the flag is set on an Anthropic-dialect provider** — rejected as noise. The
  same is already true of `OpenAiUpstreamApi` and `RequestNormalization`; singling this
  field out would be inconsistent.
- **Require `OpenAiUpstreamApi` to be set explicitly whenever the flag is on** — rejected.
  It couples the two fields that LADR-02 exists to keep apart.

## Consequences

- Positive: the validator stays a list of genuine invalid states; no rule needs revisiting
  when a fourth body-shape switch arrives.
- Positive: env-parse behaviour is uniform across all three per-provider booleans.
- Negative: a typo'd env value is silent apart from the log line. Accepted, and recorded
  here so the next reader knows it was a decision rather than an oversight.
- Neutral: if the log-and-skip default is ever revisited, this field changes with the other
  two rather than separately.

## Related

- **LADR-02** — the orthogonality that leaves no cross-field rule to write.
- **HLD 007 LADR-02** — the conventional env surface and its parse semantics.
