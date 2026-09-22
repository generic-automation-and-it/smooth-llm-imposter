# LADR-003 — Infinite client timeout, targeted retry handler

- **Date / Status:** 2026-06-14 · Accepted; amended 2026-07-06, 2026-08-30, 2026-09-22 (HLD 012)

## Context

SSE responses routinely exceed `AddStandardResilienceHandler` defaults, and retrying a
partially-streamed POST would duplicate or corrupt output.

## Decision

The `imposter-upstream` named client uses `Timeout.InfiniteTimeSpan`; the request is bounded by
the caller's `RequestAborted` token. No standard resilience handler is attached.

The client does attach a narrow retry handler for pre-response outbound transport failures and attempt
timeouts. It uses `HttpRetryStrategyOptions` with `ShouldHandle` narrowed to `HttpRequestException` or
`TimeoutRejectedException`, retries twice with fixed delays of 1s and 2s, and gives the initial attempt and
two retries a header wait of base ×1, ×2, and ×3. The base is the route's `TimeoutSeconds` when the
provider declares one, otherwise `DependencyInjection.DefaultUpstreamTimeoutSeconds` (300s) — so the
shipped ladder is 300s, 600s, 900s (HLD 012; it was a fixed 200s/600s/900s before that). `Retry-After` is ignored so the
operator-visible delay sequence stays deterministic. Upstream 5xx/408/429 HTTP responses are not retried
for LLM POSTs, because the upstream may already have processed and billed the request.

## Consequences

Transient upstream transport errors can be retried before the Host maps the final failure to a 502
dialect-shaped envelope. Maximum header wait is base ×6 plus 3s retry delay — 1,800s on the shipped
300s base — while caller's
`CancellationToken` still bounds all attempts and delays. Infinite client timeout remains unchanged for
long-lived SSE responses. `HttpCompletionOption.ResponseHeadersRead` keeps retry scope at response
headers, so body-stream failures do not replay a partially delivered response.
