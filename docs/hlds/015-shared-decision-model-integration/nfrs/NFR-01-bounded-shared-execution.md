# NFR-01: Bounded shared execution

**Status:** Accepted

## Requirement

For one Host process and one service profile, admitted HTTP decision calls never overlap, including retries. Let T be TimeoutSeconds and A be MaxAttempts: admission wait is bounded by T, each complete successful response attempt by T, and attempts by A. With no deliberate retry delay, the configured waiting budget per call is at most T plus A × T, excluding local scheduling/serialization overhead. Defaults give 60 seconds admission plus 180 seconds attempt budget.

## Verification

Drive separate consumer fixtures and DI scopes against a capturing service, including stalls before headers and during the successful body. Measure active-call count, attempt count and admission/body timeouts. Exercise caller cancellation during admission and execution, then submit a succeeding call.

## Acceptance Criteria

- Maximum admitted concurrency is one; no path bypasses the gate.
- Admission and body stalls fail within the configured budgets, with an explicit test tolerance.
- 504 and malformed-envelope cases make exactly one attempt.
- Cancellation releases local resources without retry and permits the next call.

## Applies To

Shared transport, gate and failure contract; Goals 1 and 2.
