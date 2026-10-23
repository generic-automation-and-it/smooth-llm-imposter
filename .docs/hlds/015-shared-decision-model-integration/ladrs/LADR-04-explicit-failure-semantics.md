# LADR-04: Explicit failure and cancellation under bounded transport

**Status:** Draft

## Context

- Tool filtering fails open, but another consumer may need a different response to service failure.
- ResponseHeadersRead alone does not bound a stalled successful response body.
- OpenJEV gateway 504s were repeatable for oversized identical requests and may leave remote work running.

## Decision

**Report** transport, unsuccessful status, malformed envelope and admission failures to the caller; do not manufacture answers or choose a fallback action. Preserve caller cancellation as cancellation, without converting it into service failure or retrying it. Consumers decide whether a failure means continue unchanged, defer or reject.

Bound each complete successful HTTP response attempt by the configured timeout. Maximum attempts includes the first attempt. Retry only transport errors, attempt timeouts, 502 and 503; do not retry 504, other statuses, or envelope parsing. The preserved retry schedule adds no deliberate delay; changing that operational policy requires evidence and an explicit decision. A local retry does not establish remote cancellation or exactly-once execution.

The response must be an object containing an answers object. Return its answer keys and fields unchanged; missing keys, unknown choices, invalid probabilities and confidence interpretation belong to consumers. HLD 014 keeps its existing fail-open and partial-chunk semantics.

## Consequences

- Consumers can distinguish missing evidence from an actual model recommendation.
- Stalled response bodies cannot occupy the gate indefinitely.
- Retries can duplicate remote evaluation; exactly-once semantics are not promised.
