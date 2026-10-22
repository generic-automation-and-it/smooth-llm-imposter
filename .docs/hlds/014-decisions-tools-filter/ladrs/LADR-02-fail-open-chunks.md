# LADR-02: Fail-open unresolved chunks

**Status:** Accepted

## Context

- Classification is optional optimization, not a prerequisite for inference.
- A serialized decision service can time out or return an invalid envelope.
- Discarding earlier successful decisions would waste already-paid classification latency.

## Decision

**Keep** tools whose classification is uncertain. Failure of a chunk stops the pass and preserves that chunk plus all remaining tools; earlier answered chunks remain applied. Invalid or missing per-tool probabilities likewise keep that tool.

Caller-token cancellation propagates instead of becoming fail-open. Other failures emit a Warning carrying the reason type, never exception text or prompt content. A successful classified pass emits one Information summary of path, kept/total and elapsed milliseconds; skipped/disabled passes remain Debug-only.

## Consequences

- Classifier outages do not prevent inference.
- Partial passes can still reduce catalogs.
- Cancellation cannot accidentally launch a costly inference request after the caller leaves.
