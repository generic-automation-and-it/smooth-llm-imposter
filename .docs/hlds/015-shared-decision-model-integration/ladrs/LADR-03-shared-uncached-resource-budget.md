# LADR-03: One uncached, process-wide service budget

**Status:** Draft

## Context

- HLD 014's OpenJEV probes show serialization and gateway timeouts under parallel load.
- A gate per consumer or DI scope would multiply load instead of sharing the service budget.
- Consumer-defined state and questions make reuse of previous answers a separate semantic decision.

## Decision

**Share** one process-wide concurrency gate across all consumer calls to the configured service. At most one call is admitted at a time, including retries. Admission is bounded by the configured timeout and caller cancellation. Release the budget on success, exhausted failure and cancellation; consumer batching must not retain admission across multiple calls.

Keep the integration uncached and without durable results. This preserves the deliberate exception to the repository's cached/scoped singular-client convention recorded in [HLD 014 LADR-04](../../014-decisions-tools-filter/ladrs/LADR-04-singleton-gate.md). Neither a scoped client nor a per-feature gate may turn the process-wide limit into several independent limits.

The gate is a process-wide component in its own right, never a field on whichever client happens to be registered singleton: a per-consumer client registration would otherwise multiply the budget NFR-01 caps.

This is a local budget, not distributed coordination or a fairness guarantee. The gate bounds locally admitted calls; it cannot prove that a remote request stopped executing after a timeout or disconnect.

## Consequences

- Independent consumers cannot accidentally overlap admitted decision calls.
- Head-of-line blocking and admission timeout are accepted costs.
- Multiple Host processes can still contend at the same remote service.

## Open

- Revisit cross-process scheduling or caching only after measured contention or a consumer requirement justifies it.
