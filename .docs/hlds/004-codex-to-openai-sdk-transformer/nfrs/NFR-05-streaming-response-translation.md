# NFR-05: Streaming integrity of the Chat→Responses response translation

**Status:** Draft

<!-- One file per quality attribute. Horizontal concern spanning the whole HLD.
Status lifecycle: Draft → Prototype → Accepted. -->

## Requirement

On the bidirectional bridge path (LADR-05) the upstream Chat Completions stream is translated to
Responses events under these guarantees:

1. **Incremental** — each Responses event is emitted as soon as its source Chat chunk arrives; the
   response is never read to completion before output begins (no `ReadToEnd`, no full-body buffer).
2. **Bounded, forward-only state** — per-stream state is O(active output items + open tool-call
   arguments); no whole-stream accumulation, look-back, or replay.
3. **Streaming semantics preserved** — caller cancellation (`RequestAborted`) and the infinite
   upstream timeout behave exactly as on the byte-relay path; a mid-stream caller disconnect is
   swallowed, not retried (mirrors the existing guard).
4. **Well-formed terminal** — the emitted sequence is a valid Responses stream ending in exactly one
   `response.completed` (or a Responses-shaped error event), carrying the assembled output items and
   usage; tool calls surface as `function_call` items with streamed arguments.
5. **Off-path unchanged** — every route that is *not* the downgraded `/responses`→chat path stays
   byte-for-byte identical (defers to NFR-01).

## Verification

- **L0** unit: feed a recorded Chat chunk sequence (text deltas, `reasoning_content`, tool-call
  argument deltas, final `usage` + `[DONE]`) and assert the exact ordered Responses event sequence,
  including the single terminal `response.completed`.
- **L0** unit: assert the translator is an incremental stream transform (async sequence in/out), not
  a buffer-then-map — a synthetic never-ending input still yields early output.
- **L2** integration: the imposter `/responses`→chat path returns Responses-shaped SSE from a stubbed
  Chat upstream; an off-path response is byte-identical (shared with NFR-01).
- **L3** live (`pr_evals_gate`): a Responses-mode client turn (text and tool-using) against
  `opencode-go` renders output end-to-end.

## Acceptance Criteria

- A Responses-dialect client (e.g. Codex) consumes the translated stream and renders both a text turn
  and a tool-using turn; the first event is emitted before the upstream stream completes.
- No code path buffers the full upstream response on the translation path.
- Non-downgraded routes show no response-byte diff versus the byte-relay build.

## Applies To

LADR-05; the response-side translator in the Application layer and the Host streaming copy. Narrows —
does not break — HLD 001 LADR-003, and scopes NFR-01.
