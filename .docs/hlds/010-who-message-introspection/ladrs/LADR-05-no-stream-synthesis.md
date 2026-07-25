# LADR-05: No SSE synthesis — streaming requests pass through

**Status:** Accepted

## Context

Streaming chat replies use a per-dialect chunked delta protocol (OpenAI `data: {...}\n\n`
Chat Completions SSE, Anthropic `event: ... \ndata: {...}\n\n` Messages SSE). Synthesizing
either protocol for a one-line probe is a non-trivial amount of code that must stay
byte-consistent with the real transformer on every upstream format change — and streaming
callers can simply re-issue the probe as a non-streaming request.

## Decision

**Do not synthesize streaming responses.** When the inbound request has `stream: true`
(at the top level of the JSON body), the responder returns *no match* and the request
is forwarded to the upstream exactly as it is today — regardless of whether the last
user message is `who?`.

This is a hard rule. The responder does not attempt to detect SSE callers by header
(`Accept: text/event-stream`) either, because the forwarder's own SSE-vs-non-SSE path
is driven by the body's `stream` field, not the Accept header, and matching on the body
keeps the rule trivially stateable.

## Alternatives Considered

- **Synthesize SSE per dialect** — rejected: high maintenance cost, high drift risk
  against `ChatToResponsesStreamTransformer`, no benefit a non-streaming probe cannot
  deliver.
- **Synthesize SSE for OpenAI only** (simpler protocol) — rejected: asymmetric behavior
  across dialects is harder to document and test than a uniform pass-through rule.
- **Detect SSE by Accept header** — rejected: the forwarder keys off the body's `stream`
  field, so Accept-based detection would disagree with the forward path on edge cases.

## Consequences

- Positive: the short-circuit path has no SSE code; the real transformers remain the
  only writers of chunked deltas.
- Positive: the rule is trivially stateable in user docs: "streaming probes forward;
  send non-streaming for the reply."
- Negative: a streaming caller who cannot easily switch off streaming (a hardcoded
  agent loop) cannot use the probe; accepted because the probe is for ad-hoc
  debugging, not production dispatch.
- Neutral: integration tests cover both branches — `stream:true` forwards, `stream:false`
  intercepts.

## Related

- **LADR-02** — the trigger shape does not change across streaming/non-streaming.
- **NFR-01** — streaming non-match is byte-identical to pre-HLD streaming.
