# LADR-01: Drop the whole reasoning item, not just the `encrypted_content` property

**Status:** Accepted

## Context

In OpenAI's ZDR / stateless mode a replayed `reasoning` input item carries the model's
reasoning tokens as ciphertext under `encrypted_content`. A typical Codex replay looks
like:

```json
{"type":"reasoning","id":"rs_…","encrypted_content":[{"type":"encrypted_content","data":"…"}],"summary":[]}
```

The `summary` array is usually empty in that mode — the ciphertext *is* the payload. An
upstream that cannot decrypt rejects the request outright, so something must be removed.
Two shapes are available: delete the property and forward an emptied reasoning item, or
delete the item.

## Decision

**Remove the entire item** from the `input` array when its `type` is `reasoning` and its
`encrypted_content` is a non-null JSON node.

An item whose `encrypted_content` is absent, or present but JSON `null`, is **not**
treated as ZDR content and survives byte-for-byte. Some clients serialize the property
unconditionally and leave it null when `store: true`; keying on presence alone would
delete a legitimate plaintext summary along with it.

## Alternatives Considered

- **Strip only the `encrypted_content` property, keep the item** — rejected. In ZDR mode
  the remaining item is `{"type":"reasoning","id":…,"summary":[]}`, which carries no
  information the upstream can use and is a shape no upstream is guaranteed to accept.
  It converts a clear 400 into a per-upstream compatibility question for zero benefit.
- **Decrypt and forward plaintext** — rejected, and out of scope by construction. The key
  is held server-side by OpenAI; no key exists in a stateless router. See NFR-01.
- **Drop every `reasoning` item unconditionally when the flag is on** — rejected. It
  would discard plaintext summaries that a non-OpenAI upstream can legitimately read,
  making the option lossier than the problem requires.
- **Replace the item with a plaintext placeholder** ("[reasoning omitted]") — rejected.
  Fabricating transcript content in a transparent proxy is a larger behavioural
  commitment than dropping, and would need its own dialect/shape decisions.

## Consequences

- Positive: one predicate, one action; the rule states in a sentence.
- Positive: the strip is narrow — plaintext reasoning, summaries, and every non-reasoning
  item are untouched, so the option stays safe to leave on.
- Negative: reasoning continuity across turns is lost for that upstream. Accepted: the
  ciphertext was never usable by it, so nothing recoverable is discarded.
- Neutral: because whole items disappear, an upstream that enforces reasoning/function-call
  adjacency sees a transcript with no reasoning items at all rather than a partial one —
  the simpler of the two shapes to validate against.

## Related

- **LADR-02** — the opt-in that gates this behaviour.
- **NFR-01** — the no-decryption / no-logging boundary this decision sits inside.
