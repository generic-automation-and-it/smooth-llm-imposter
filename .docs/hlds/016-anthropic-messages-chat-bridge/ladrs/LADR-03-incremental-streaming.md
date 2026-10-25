# LADR-03: Incremental, bounded-state streaming translation

**Status:** Accepted

## Context

- Claude Code consumes Messages SSE and is sensitive to event order, block indexes and the terminal events.
- Chat upstreams stream flat deltas; tool arguments arrive fragmented, interleaved by index, or whole.
- The existing Chat-to-Responses relay already translates line by line with bounded state.

## Decision

**Adopt** line-by-line translation that emits Messages events as each Chat chunk arrives, holding only per-stream state (open block, per-index pending tool calls, usage snapshot, stop reason). The full response is never buffered, except for one bounded hold: a tool call's arguments until that call completes ([LADR-04](LADR-04-validated-tool-arguments.md)).

The translation always terminates the stream with a well-formed close (`message_delta`, `message_stop`) or a Messages error event, including on a premature upstream end or a missing finish reason. A non-streaming caller request is served with a non-streaming upstream request; the two mappings share their rules.

## Consequences

- First-token latency equals upstream latency plus one chunk.
- A second state machine (Messages events) exists next to the Responses one; they share no output shape.
- Malformed upstream chunks degrade to a defined behavior in the spec, never a hung stream.
