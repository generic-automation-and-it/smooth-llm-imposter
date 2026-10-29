# LADR-08: Optimizations as strategies, run concurrently, edits merged last

**Status:** Draft

## Context

- Tool selection and message optimization read the same body and change disjoint parts of it: `tools`/`tool_choice` versus message text.
- Running them one after another adds their latencies; each already has its own switch, models and failure handling.
- The tool filter today returns a whole rewritten body, but already changes only `tools` and `tool_choice` byte-preservingly (HLD 014 LADR-07).

## Decision

**Model each optimization as a strategy behind one interface: given the request, return an edit to its own part of the body, or no change.** A strategy's own settings decide whether it is on (`LLM_MESSAGES_ENABLED` for messages, `DECISIONS_TOOLS_ENABLED` for tools). A coordinator keeps the strategies that are on, runs them concurrently and waits for all of them, then merges their edits into the original bytes once. Host calls only the coordinator. Strategies are registered explicitly (NativeAOT; no assembly scanning).

Strategy is chosen for the interchangeable, config-selected units; the concurrency belongs to the coordinator. Chain of Responsibility was the obvious alternative and runs units in sequence; Command adds queueing and undo nothing here needs; Mediator suits collaborators that talk to each other, and these do not.

**Branches are independent.** A strategy that fails, times out or is cancelled contributes no edit and the others still apply. If two edits ever overlap, the original body is forwarded. Caller cancellation cancels every branch. Inside the message strategy, messages are processed one at a time, never batched (NFR-04).

## Consequences

- Adding an optimization means adding a strategy, not editing the seam.
- The tool filter's contract changes from "return a body" to "return an edit"; its behaviour does not.
- Parallelism is bounded by one process-wide local-LLM slot: every decision and optimizer call takes turns, whatever endpoint or model it uses (FR-13 of #164, HLD 015 LADR-05 amended 2026-10-29).
