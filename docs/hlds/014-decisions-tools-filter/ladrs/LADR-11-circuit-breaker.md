# LADR-11: Circuit breaker on tool-filter classifier calls

**Status:** Accepted (2026-10-25) — implemented

## Context

- Fail-open (LADR-02) keeps inference correct when the decision service fails, but not fast. Every
  tool-filtered request still waits for the gate and spends up to `MaxAttempts` × `TimeoutSeconds`
  (180 seconds at the defaults, NFR-03) before the catalog is forwarded unfiltered.
- A dead or misconfigured classifier fails the same way on every request. The 2026-10-23 field run
  answered `400 invalidModelIdentifier` to every pass because the configured model was not a decision
  model; an unreachable host times out on every pass.
- A request-side message optimizer ([#164](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/164))
  will call the decision service with its own model and possibly its own endpoint, so one consumer's
  outage says nothing about the other's.

## Decision

**Break the circuit after one failed classifier call and skip the filter for a cooldown.**

- **Scope: the tool-filter consumer only.** Other decision-service consumers keep their own circuit,
  because they can use different models and endpoints.
- **What counts as a failure:** one logical classifier call that ends in failure after its retries —
  transport errors, attempt timeouts, 5xx responses (including 504), and 4xx responses except 408 and 429, which signal a
  configuration error such as an unknown model. Caller cancellation is not a failure, and neither is a
  local gate-wait timeout (contention, not a service fault).
- **Trip rule:** a failure ratio of 1.0 with a minimum throughput of 1 in a 30-second window — a single
  failed call opens the circuit.
- **Placement:** outside the retries, so an exhausted call counts once and an open circuit spends no
  attempts. An open circuit short-circuits before the process-wide gate is acquired, so it spends no
  gate wait either.
- **While open:** no classifier request. The pass is skipped and the catalog forwarded unchanged — the
  same outcome as fail-open — with skip reason `circuit-open`.
- **After the cooldown:** one trial call goes through. Success closes the circuit; failure reopens it
  for another cooldown.
- **Cooldown setting:** optional `DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS`
  (`Imposter:DecisionsTools:CircuitBreakSeconds`), default 180 seconds, validated like the other
  timeouts. When #164 adds the shared `DECISIONS_*` defaults, `DECISIONS_CIRCUIT_BREAK_SECONDS` supplies
  the default and the `DECISIONS_TOOLS_` value overrides it.
- **Logging is loud:** opening the circuit logs at **Error** — consumer, configured model, classifier
  endpoint (userinfo and query dropped, as for existing outcome logs), the failure cause (HTTP status or
  exception type, never exception text or prompt content) and the time it will retry. The trial call and
  the close log at Information, so recovery is visible too.
- **NativeAOT:** the breaker must publish warning-free and be verified on the native executable. For
  example with tech stack: Polly v8's circuit-breaker strategy in the existing decision-service
  resilience pipeline — team's call, not fixed by this LADR. HLD 015 keeps transport ownership; this
  LADR fixes only the tool filter's breaker policy and outcome.

## Consequences

- A classifier outage costs at most one failed call per cooldown instead of one per request.
- Tool filtering stops for up to the cooldown after a single transient failure; the catalog is
  forwarded whole meanwhile, which is the safe direction.
- A second decision-service consumer needs its own breaker instance, so a shared resilience pipeline
  cannot hold one process-wide breaker for all consumers.

## Implementation outcome (2026-10-25)

Implemented as an Application-owned consumer singleton (`DecisionCircuitBreaker`, `Features/Routing/Decisions`) consulted by the filter before the client call, so an open circuit never reaches the gate. Failure classification treats 4xx as a configuration fault except 408 and 429, which are transient. A dedicated `DecisionServiceTimeoutException` (wrapped by the transport from the resilience library's timeout) and `DecisionGateTimeoutException` (thrown by the gate) keep classification off exception text. After the cooldown one caller wins a trial admission; a cancelled trial clears the admission so it cannot wedge the circuit, and only a trial's success closes the circuit (a pass admitted before the open cannot close it).

## Revisit when

Measured transient failures trip the circuit often enough that the lost filtering matters — then raise
the minimum throughput rather than lengthening the window.
