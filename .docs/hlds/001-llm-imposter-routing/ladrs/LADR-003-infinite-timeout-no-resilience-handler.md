# LADR-003 — Infinite client timeout, targeted retry handler

- **Date / Status:** 2026-06-14 · Accepted; amended 2026-07-06

## Context

SSE responses routinely exceed `AddStandardResilienceHandler` defaults, and retrying a
partially-streamed POST would duplicate or corrupt output.

## Decision

The `imposter-upstream` named client uses `Timeout.InfiniteTimeSpan`; the request is bounded by
the caller's `RequestAborted` token. No standard resilience handler is attached.

The client does attach a narrow retry handler for pre-response outbound transport failures. It uses
`HttpRetryStrategyOptions` with `ShouldHandle` narrowed to `HttpRequestException` and retries three
times with fixed delays of 1s, 2s, and 5s. `Retry-After` is ignored so the operator-visible delay
sequence stays deterministic. Upstream 5xx/408/429 HTTP responses are not retried for LLM POSTs,
because the upstream may already have processed and billed the request.

## Consequences

Transient upstream transport errors can be retried before the Host maps the final failure to a 502
dialect-shaped envelope. The maximum retry sleep overhead is 1 + 2 + 5 = 8s on top of upstream
latency, and the caller's `CancellationToken` still bounds all attempts and delays. The infinite
timeout remains unchanged for long-lived SSE responses. `HttpCompletionOption.ResponseHeadersRead`
keeps the retry scope at response headers, so body-stream failures do not replay a partially
delivered response.
