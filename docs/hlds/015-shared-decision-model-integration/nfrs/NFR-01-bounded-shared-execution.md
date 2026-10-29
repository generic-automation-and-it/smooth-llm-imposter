# NFR-01: Bounded shared execution

**Status:** Accepted

## Requirement

For one Host process, admitted decision and optimizer calls never overlap, including retries, whatever endpoint or model they use ([LADR-05](../ladrs/LADR-05-per-section-profiles-circuits-gates.md), amended 2026-10-29). Let T be a section's TimeoutSeconds, A its MaxAttempts and D its RetryDelaySeconds: admission wait is bounded by T, each complete successful response attempt by T, and attempts by A. The configured waiting budget per decision call is at most T + A × T + (A − 1) × D, excluding local scheduling/serialization overhead. Defaults give 60 seconds admission plus 125 seconds attempt budget (2 × 60 + 5).

## Verification

Drive separate consumer fixtures and DI scopes against a capturing service, including stalls before headers and during the successful body. Measure active-call count, attempt count and admission/body timeouts. Exercise caller cancellation during admission and execution, then submit a succeeding call.

## Acceptance Criteria

- Maximum admitted concurrency is one per gate key; every call passes through its key's gate.
- Admission and body stalls fail within the configured budgets, with an explicit test tolerance.
- 504 and malformed-envelope cases make exactly one attempt.
- Cancellation releases local resources without retry and permits the next call.

## Applies To

Shared transport, gate and failure contract; Goals 1 and 2.
