# LADR-003 — Infinite client timeout, targeted retry handler

- **Date / Status:** 2026-06-14 · Accepted; amended 2026-07-06

## Context

SSE responses routinely exceed `AddStandardResilienceHandler` defaults, and retrying a
partially-streamed POST would duplicate or corrupt output.

## Decision

The `imposter-upstream` named client uses `Timeout.InfiniteTimeSpan`; the request is bounded by
the caller's `RequestAborted` token. No standard resilience handler is attached.

The client does attach a narrow retry handler for transient outbound HTTP failures. It uses
`HttpRetryStrategyOptions` transient detection and retries three times with fixed delays of 1s,
2s, and 5s. `Retry-After` is ignored so the operator-visible delay sequence stays deterministic.

## Consequences

Transient upstream transport errors can be retried before the Host maps the final failure to a
502 dialect-shaped envelope. The infinite timeout remains unchanged for long-lived SSE responses.
