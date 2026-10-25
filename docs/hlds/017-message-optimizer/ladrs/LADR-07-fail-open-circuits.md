# LADR-07: Per-section circuits; failures move to the next step

**Status:** Draft

## Context

- Fail-open keeps inference correct, but a dead model still costs every request its full retry budget.
- The gate and the optimizer can use different models and endpoints, so one failing says nothing about the other.
- HLD 014 LADR-11 already defines a one-failure circuit for the tool filter.

## Decision

**Give the message gate and the optimizer each their own circuit**, following HLD 014 LADR-11's rule: one failed call after retries opens it — transport errors, attempt timeouts, 4xx and 5xx responses — while caller cancellation, local gate-wait timeouts and an invalid gate answer (which continues to the optimizer) do not. An open circuit makes no call. After the cooldown (optional `CIRCUIT_BREAK_SECONDS`, default 180) one trial call closes or reopens it.

**A failed or open gate continues to the optimizer; a failed or open optimizer forwards the original.** No failure here changes the caller-visible response. Opening logs at Error with section, configured model, endpoint (userinfo and query dropped), cause (HTTP status or exception type, never exception text or message content) and retry time; the trial and the close log at Information.

## Consequences

- An outage costs at most one failed call per section per cooldown (NFR-03).
- A down gate sends every candidate to the optimizer, raising optimizer load and latency (Open item 7).
- A transient failure stops optimization for up to a cooldown, which is the safe direction.

## Related

- **HLD 014 LADR-11** — same trip rule for the tool filter.
- **HLD 015 amendment** — per-section circuits and per-endpoint admission gates.
