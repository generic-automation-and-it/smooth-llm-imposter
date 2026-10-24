# LADR-04: Tool-call arguments are held, validated, then emitted

**Status:** Draft

## Context

- Claude Code executes tools from the accumulated `input` JSON; a wrong or empty input runs the wrong action (an empty `Write`).
- Chat upstreams fragment arguments, send them whole, interleave parallel calls, or truncate them at the token limit, and some emit malformed JSON.
- Messages clients do not need tool input to stream incrementally; they parse it when the block stops.

## Decision

**Adopt** holding each tool call's argument text until that call is complete, then validating it, then emitting it as a single input delta. Empty arguments become `{}`; a bounded repair may close unterminated strings and brackets, never invent content; anything still invalid is never replaced by `{}`: a call cut off by the length limit is dropped (the stop reason then follows the blocks that survive), any other invalid call fails the message with a Messages error. Argument numbers keep their textual form.

Calls are emitted one after another in upstream index order, so Messages blocks never overlap. While arguments are held the stream stays alive with pings.

## Consequences

- Long tool inputs reach the client at the end of the call, not progressively.
- Memory per stream is bounded by the held argument size, which has a cap ([NFR-02](../nfrs/NFR-02-stream-latency-and-memory.md)).
- A model that emits broken JSON produces a visible error the client can retry, instead of a silently wrong tool run. A deterministic upstream defect will be retried by the client and spend upstream tokens each time.
