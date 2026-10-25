# NFR-05: Classifier outage cost

**Status:** Accepted (2026-10-25) — not yet implemented

## Requirement

A failing decision service costs the tool filter at most one failed classifier call per cooldown (default
180 seconds, LADR-11). While the circuit is open, a tool-filtered request makes no classifier request,
takes no gate and spends no attempt time, and its catalog is forwarded unchanged. Opening the circuit is
logged at Error, and the trial call and the close at Information, without exception text or prompt
content.

## Verification

L0 drives the real resilience pipeline with a stub transport: one exhausted call opens the circuit; the
next request makes no transport call and acquires no gate; after the cooldown, one trial call goes
through, and a success closes the circuit while a failure reopens it. Use a short configured cooldown so
the test does not wait 180 seconds. A native smoke run against an unreachable classifier shows one
failed pass followed by immediate unfiltered forwards and the Error line.

## Acceptance Criteria

- After one failed call, requests within the cooldown make zero classifier requests and forward the
  catalog unchanged.
- Transport errors, timeouts, 4xx and 5xx responses open the circuit; caller cancellation and gate-wait
  timeouts do not.
- A successful trial call closes the circuit; a failed one reopens it for a full cooldown.
- `DECISIONS_TOOLS_CIRCUIT_BREAK_SECONDS` is optional, defaults to 180 and is validated at startup.
- The open event logs at Error with consumer, model, endpoint, cause and retry time; no raw prompt or
  exception text.
- The breaker behaves the same on the NativeAOT executable as under JIT.

## Applies To

Decision-client resilience for the tool-filter consumer, filter skip logging and native smoke checks.
