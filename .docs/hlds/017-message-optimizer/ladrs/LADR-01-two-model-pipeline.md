# LADR-01: Classifier gate and generation optimizer on separate transports

**Status:** Draft

## Context

- The decision service is a classifier: model, state and keyed questions in, per-question probabilities out (HLD 015). It cannot produce rewritten text.
- A rewrite needs a generation model, which may live on a different endpoint, model and credential.
- Sharing one transport would make a classifier setting silently configure the rewriter, or the reverse.

## Decision

**Use two models on two transports.** The gate is a consumer of the HLD 015 decision integration: one question, "can this message be optimized?", answered by a probability compared inclusively with the section's `MIN_PROBABILITY`; a missing or invalid answer continues to the optimizer, as a failed call does, but does not open the circuit — HLD 014 LADR-11 counts no malformed answers. The optimizer is a separate generation endpoint configured only by `LLM_MESSAGES_*`, called with a non-streaming OpenAI Chat Completions–compatible request (default path `/v1/chat/completions`) and an optional bearer `API_KEY`.

The optimizer never inherits a decision setting, because pointing a rewrite at the classifier endpoint would fail on every call.

## Consequences

- Each model can be tuned, moved or disabled independently.
- Two endpoints to operate, and two egress destinations (NFR-04).
- The optimizer's response is complete before forwarding starts, so a rewrite adds its full generation time to time-to-first-byte (Open item 7).

## Related

- **HLD 015** — the gate is a new consumer; its amendment is a dependency.
- **LADR-06** — how both setting sets resolve.
