# LADR-003 — Infinite client timeout, no resilience handler

- **Date / Status:** 2026-06-14 · Accepted

## Context

SSE responses routinely exceed `AddStandardResilienceHandler` defaults, and retrying a
partially-streamed POST would duplicate or corrupt output.

## Decision

The `imposter-upstream` named client uses `Timeout.InfiniteTimeSpan`; the request is bounded by
the caller's `RequestAborted` token. No standard resilience handler is attached.

## Consequences

No automatic retry on transient upstream errors; transport failures map to a 502 dialect-shaped
envelope. Add targeted retry only on the pre-response (connect) phase if needed later.
