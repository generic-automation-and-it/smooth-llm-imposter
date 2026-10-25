# NFR-03: Failure cost

**Status:** Draft

## Requirement

A failing gate or optimizer costs at most one failed call per section per cooldown (default 180 s). While a circuit is open, its section makes zero calls and takes no admission-gate wait. No gate, optimizer, validation or circuit outcome changes the caller-visible HTTP status, headers or body shape.

## Verification

L0 drives the real resilience pipeline with a stub transport and a short configured cooldown: one exhausted call opens the circuit, the next request makes no transport call, and a trial after the cooldown closes or reopens it — for the gate and the optimizer separately. L2 asserts the four failure combinations (gate down, optimizer down, both, neither) all reach the upstream with a valid body, and one case per branch failing alone asserts the other branch's edit is still applied. A native smoke against unreachable gate and optimizer endpoints shows one failed call per section, immediate forwards afterwards, and the Error lines.

## Acceptance Criteria

- Gate down → optimizer still called; optimizer down → original forwarded; both down → original forwarded.
- Caller cancellation and gate-wait timeouts open no circuit.
- The Error line carries section, model, endpoint, cause and retry time, and no message content.

## Applies To

Goal 5; LADR-07.
