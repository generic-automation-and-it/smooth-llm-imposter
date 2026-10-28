# NFR-03: Failure cost

**Status:** Draft

## Requirement

A failing gate or optimizer costs at most one failed call per section per cooldown (default 180 s). While a circuit is open, its section makes zero calls and takes no admission-gate wait. No gate, optimizer, validation or circuit outcome changes the caller-visible HTTP status, headers or body shape.

## Verification

L0 drives the real resilience pipeline with a stub transport and a short configured cooldown: one exhausted call opens the circuit, the next request makes no transport call, and a trial after the cooldown closes or reopens it — for the gate and the optimizer separately. L2 asserts the four failure combinations (gate down, optimizer down, both, neither) all reach the upstream with a valid body, and one case per branch failing alone asserts the other branch's edit is still applied. A native smoke against unreachable gate and optimizer endpoints shows one failed call per section, immediate forwards afterwards, and the Error lines.

Implemented as an L2 file, `MessageOptimizerFailureCostTests`, that runs the real decision and optimizer clients and their resilience pipelines with only the transports stubbed and a manual clock for the cooldown (placed at L2 rather than L0 because only the Host test project references both layers and the public DI surface). It asserts: all four failure combinations (and a transport-throw variant) reach the upstream with a valid body and an unchanged caller response; per section, one exhausted call opens the circuit, the next requests make zero transport calls for that section while the other section keeps being asked, one trial runs after the cooldown, a failed trial reopens it and a successful one closes it; exactly one Error line carries section, model, endpoint, cause and retry time with no message text at Information and above; the tool branch failing alone leaves the message edit applied and the message stage failing alone leaves the tool edit applied; and caller cancellation mid-call opens no circuit. Gate-wait timeouts are covered by the existing L0 strategy and breaker tests, because the real gate wait needs contention the in-process host cannot create without sleeps. The retry budget is pinned to one attempt so the manual clock never has to drive Polly delays; the per-attempt retry ladder stays covered by the Infrastructure client tests. A host-platform NativeAOT smoke against unreachable gate and optimizer ports logged one Error per section, then `circuit open` skip lines with immediate forwards, and a counting stand-in that answers 500 saw exactly one gate call and one optimizer call across three requests. `linux/amd64` and `linux/arm64` remain native-runner gates.

## Acceptance Criteria

- Gate down → optimizer still called; optimizer down → original forwarded; both down → original forwarded.
- Caller cancellation, gate-wait timeouts, optimizer admission timeouts and per-request budget expiry open no circuit.
- The Error line carries section, model, endpoint, cause and retry time, and no message content.

## Applies To

Goal 5; LADR-07.
