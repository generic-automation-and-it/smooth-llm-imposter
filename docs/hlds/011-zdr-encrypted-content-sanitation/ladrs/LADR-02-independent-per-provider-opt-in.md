# LADR-02: Independent per-provider opt-in — not coupled to `OpenAiUpstreamApi`, `RequestNormalization`, or `IsImposter`

**Status:** Accepted

## Context

The router already carries two per-provider body-shape switches on the OpenAI dialect:

- `OpenAiUpstreamApi` (`responses` | `chat_completions`) — selects the upstream endpoint
  and, for `chat_completions`, triggers the Responses→Chat downgrade.
- `RequestNormalization` (`codex_to_openai_sdk` | `none`, HLD 004) — reshapes a Codex
  request into the OpenAI SDK's Chat tool contract. It is gated on `IsImposter`, defaults
  on for `chat_completions`, and the validator **forbids** it on a `responses` provider.

ZDR sanitation looks superficially like a third normalization profile, and the motivating
provider (LM Studio) happens to be an imposter route. Reusing either existing switch was
therefore a live option.

## Decision

`StripEncryptedContent` is its **own** per-provider option, and:

1. **Not derived from `OpenAiUpstreamApi`.** The motivating case explicitly wants to keep
   using `/responses`. A `chat_completions` downgrade does not solve the problem it was
   asked to solve.
2. **Not a `RequestNormalization` profile.** Normalization is forbidden on `responses`
   providers by the validator, which is exactly where this strip must work.
3. **Not gated on `IsImposter`.** It is a plain
   `decision.Provider.StripEncryptedContent == true` branch, mirroring how
   `OpenAiUpstreamApi` is read in `Transform` — an upstream's inability to decrypt is a
   property of that upstream, not of whether the model name was rewritten.
4. **Composable, ordered.** The strip runs after normalization and before
   `ToChatCompletions`, so both the `responses` and `chat_completions` paths get it and
   the downgrade only ever sees survivors.

## Alternatives Considered

- **A third `RequestNormalization` value (`strip_zdr`)** — rejected. The values are
  mutually exclusive, so an operator could not have both normalization and stripping;
  and the validator's `responses`-forbidden rule would have to be special-cased, making
  one field mean two unrelated things.
- **Infer it from `OpenAiUpstreamApi: responses` + `IsImposter`** — rejected. Silent
  behaviour keyed off unrelated fields; a real-OpenAI passthrough or a `responses`
  imposter that *can* decrypt would be stripped without asking.
- **A global (root-level) toggle** — rejected. Capability differs per upstream; one
  provider needing the strip must not change another's forward path.
- **React to the upstream 400 and retry stripped** — rejected, explicitly out of scope.
  It adds a retry path, doubles latency on the failing case, and requires parsing
  upstream error prose to decide when to retry.

## Consequences

- Positive: each of the three switches has one reason to change; no field means two things.
- Positive: the option is expressible on any OpenAI-dialect provider, imposter or not.
- Positive: ordering is explicit and tested, so the downgrade path needs no ZDR awareness
  of its own.
- Negative: one more per-provider field and one more conventional env suffix to document.
  Accepted — the HLD 007 surface is designed for exactly this.
- Neutral: nothing prevents an operator setting the flag on a provider that does not need
  it; the cost is a cheap array walk and the loss of reasoning replay.

## Related

- **HLD 004** — the normalization profile this decision deliberately stays out of.
- **HLD 007 LADR-02** — the conventional env surface this option registers into.
- **HLD 008** — the runtime registry/CRUD the flag must survive to reach the route.
- **LADR-03** — why the validator gained no rule for this field.
