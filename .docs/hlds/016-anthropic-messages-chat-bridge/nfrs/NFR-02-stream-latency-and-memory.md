# NFR-02: Streaming adds negligible latency and bounded memory

**Status:** Draft

## Requirement

Translating a stream must not delay or accumulate it beyond the one held tool-call argument.

- `message_start` is written within 20 ms of the first upstream chunk being read.
- Each translated text or reasoning delta is written within 5 ms (p99) of its source chunk for chunks up to 1 KiB, excluding upstream time.
- Retained per-stream memory is the open block state plus held tool-call arguments only; held arguments are capped at 4 MiB per call and 16 MiB per stream, and an SSE event is capped at 8 MiB. Exceeding a cap ends the stream with a Messages error.
- A stream with no upstream bytes for 8 seconds still emits a ping.

## Verification

L0 harness feeding a synthetic chunk source with a fake clock; allocation measurement over a 100,000-chunk text stream and a 3 MiB single tool call.

## Acceptance Criteria

- Latency bounds hold on the CI runner for the synthetic source.
- Memory for the long text stream does not grow with chunk count.
- Cap and ping cases in the [stream spec matrix](../spec/03-stream-state-machine.md) pass.

## Applies To

Stream translation only; non-streaming requests are bounded by the existing request and response size handling.
