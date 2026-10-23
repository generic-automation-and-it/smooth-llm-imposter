# NFR-03: Serialized service budget

**Status:** Accepted

## Requirement

One process permits at most one in-flight decision chunk, including retries. Every gate wait and every complete HTTP-response attempt is bounded by TimeoutSeconds. The service's 30-second 504 is not retried; exhausted chunks stop the pass. Default MaxAttempts and TimeoutSeconds bound a hung acquired chunk to 180 seconds of attempt time (retry delay is zero); gate waiting has its separate 60-second default budget.

## Verification

L0 drives real resilience handlers with stalls before and after headers, HTTP failures and clients resolved from separate scopes. Native/container live checks use at least 60 tools and two concurrent inference requests, recording wall times and every chunk outcome. Compare cache-hit usage before/after on a caching-enabled route where the upstream reports it.

## Acceptance Criteria

- No overlapping decision requests; retry/gate counts match configuration.
- Body stalls time out, not merely header stalls.
- Live chunks stay below the gateway cutoff with no 504; record total serialized latency.
- Record cache-hit measurements, or their absence from the upstream contract.

## Applies To

Decision-client gate, complete-response resilience and native smoke checks.
