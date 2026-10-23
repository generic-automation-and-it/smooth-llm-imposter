# LADR-04: Uncached singleton client with a process-wide gate

**Status:** Accepted

## Context

- Measured parallel OpenJEV requests do not improve throughput; four concurrent 25-question requests all timed out.
- A scoped client would create a separate semaphore per inference request, making a nominal gate ineffective.
- The normal [external client rule](../../../../.agents/rules/backend/external-api-clients.instructions.md) prefers cached scoped singular clients.

## Decision

**Use** a singleton decision-client contract backed by the named decisions-tools HTTP client and one process-wide semaphore. Hold the gate for one chunk, including all retry attempts; acquire it using the configured timeout and caller cancellation. The client does not cache; the filter memoises chunk answers in memory ([LADR-07](LADR-07-chunk-memo-and-byte-preserving-output.md)), so cached chunks never take the gate. This is a deliberate exception to the cached/scoped singular-client convention.

Retry transport exceptions, attempt timeouts, 502 and 503 up to MaxAttempts minus one. Do not retry 504: an identical overloaded request still occupies the serialized service. Bound the entire successful HTTP response body, not merely header arrival; validate the buffered envelope once, without parse retries. Keep the HttpClient timeout infinite so the resilience pipeline owns the attempt budget.

## Consequences

- Independent DI scopes share the same classifier gate.
- Queue waits can fail-open without occupying OpenJEV.
- Head-of-line blocking is accepted; this is process-wide, not a distributed gate.
