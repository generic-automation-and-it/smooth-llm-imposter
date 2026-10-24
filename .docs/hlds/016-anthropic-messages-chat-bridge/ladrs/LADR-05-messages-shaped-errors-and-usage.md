# LADR-05: Errors and usage always leave in Messages shape

**Status:** Draft

## Context

- The client's retry, rate-limit, overload and compaction behavior is driven by the error envelope, its type, a few message phrasings and the `retry-after` header.
- Its context accounting and auto-compaction are driven by `usage`; a missing or zero value silently breaks them.
- Chat upstreams report neither consistently, and may truncate a stream without saying so.

## Decision

**Adopt** one rule: whatever the upstream did, the client receives a well-formed Messages error envelope (status kept, type derived from status, upstream message preserved, `retry-after` forwarded) or a well-formed message with non-empty `usage`. Context-overflow errors are re-phrased into the two forms the client recognises. A stream that ends without a finish reason and without a terminator is an error, not a clean end. Empty upstream output is translated faithfully, never turned into an error or retried.

Missing usage is estimated with one shared estimator (also used for `count_tokens` and the `message_start` input figure); streaming requests always ask the upstream for usage.

## Consequences

- Truncation is visible and retryable instead of looking like a finished turn.
- Estimated usage is approximate; it is only a fallback for upstreams that omit usage.
- The bridge performs no retry or fallback of its own; the client's retry applies.
- The router seam must give the bridge the upstream status and retry headers, not only the body, for non-success replies.
