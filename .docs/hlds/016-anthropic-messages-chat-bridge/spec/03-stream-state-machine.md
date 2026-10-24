# 03 - Streaming state machine: Chat Completions SSE -> Messages SSE (normative)

Siblings: `01-request-mapping.md` (request side, sets `stream_options.include_usage`), `02-response-mapping.md` (non-stream mapping; shares stop_reason, usage, tool-argument and name/id rules), `04-harness-and-errors.md` (HTTP-level error responses, headers, token estimator, retry-after, context-overflow message text).

Normative language: MUST / MUST NOT / SHOULD / MAY. This file describes the target behavior only. Decisions D1-D21 (`00-cross-cutting-decisions.md`) are binding and are referenced by number.

---

## 0. Contract, vocabulary, constants

### 0.1 Contract

Input to the streaming bridge: a 2xx upstream response whose body is a Chat Completions SSE stream, plus request-derived context:
- `inboundModel`: the model name the client sent (echoed in message_start, D2).
- `requestInputEstimate` (E): input token estimate of the request, computed by the request mapping with the estimator defined in 04 (D8).
- `toolNameMap`: per-request bidirectional tool-name map; the bridge uses the reverse direction (D6).
Output: Messages SSE frames written to the client, in order, one writer at a time.

Vocabulary: "bridge", "client", "upstream" as in `decisions.md`. "Frame" = one SSE event written to the client. "Block" = a content block. "Call" = one upstream tool call being assembled. "Pending call" = a call that has started and has not yet been emitted or dropped.

### 0.2 Named constants

| Name | Value | Meaning |
|---|---|---|
| PING_IDLE | 8 seconds | maximum time without any frame written (D9) |
| FINISH_GRACE | 2 seconds | maximum wait, after the first finish_reason, for a usage chunk / `[DONE]` / EOF before finalizing |
| MAX_LINE_BYTES | 8,388,608 (8 MiB) | longest accepted SSE line |
| MAX_EVENT_BYTES | 8,388,608 (8 MiB) | longest accepted SSE event (sum of its data lines) |
| MAX_JSON_DEPTH | 64 | nesting limit when parsing a chunk and when validating/repairing tool arguments |
| MAX_TOOL_CALLS | 128 | pending + emitted calls per message |
| MAX_TOOL_ARGS_BYTES | 4,194,304 (4 MiB) | held argument text per call |
| MAX_PENDING_BYTES | 16,777,216 (16 MiB) | held argument text across all pending calls |

| ERROR_MESSAGE_MAX_CHARS | 2,000 | truncation of upstream error text (D10) |
| SYNTH_ID_CHARS | 24 | random characters after `msg_` / `toolu_` (D3, D5) |

Exceeding any MAX_* limit is a stream failure (section 8, F-LIMIT).

---

## 1. Where the bridge sits

- The request mapping (01) builds a Chat request with `stream:true` and `stream_options.include_usage:true`. The forwarder returns the upstream response. Non-2xx upstream responses never reach this file (04 turns them into HTTP error responses).
- A 2xx response with `Content-Type: text/event-stream` is relayed live by the state machine below. A 2xx response with any other content type (typically `application/json`) for a streaming request MUST be parsed as a Chat JSON response, mapped by 02, and replayed to the client as a complete event sequence (message_start, ping, blocks each as start/delta/stop, message_delta, message_stop). No other buffered-replay behavior exists.
- The client response is HTTP 200 with `Content-Type: text/event-stream; charset=utf-8`, `Cache-Control: no-cache, no-transform`, `X-Accel-Buffering: no` (04 owns the complete header set). Each frame MUST be flushed to the client as soon as it is written.
- Failover or retry to another upstream is impossible once the 2xx response exists; from then on failures are `error` frames (section 8).

---

## 2. Upstream SSE ingestion

### 2.1 Line and event grammar (MUST)

1. Decode bytes with a stateful UTF-8 decoder (code points split across network reads MUST survive). Strip one U+FEFF at the very start of the stream.
2. Line terminators: LF, CRLF or lone CR. A CR at the end of the available bytes MUST wait for the next byte to decide whether it is part of CRLF.
3. An empty line dispatches the accumulated event. A line starting with `:` is a comment: ignored, but it counts as upstream liveness for the forwarder's idle accounting and does NOT start the message. A line `field:value` has exactly one leading space removed from `value`; a line without a colon is a field with an empty value. Fields `event` and `data` are honored; `id`, `retry` and unknown fields are ignored.
4. Multiple `data` lines of one event are joined with LF. An event without any `data` line is discarded. The event name defaults to `message`.
5. At EOF, an unterminated final line is treated as a line, and a pending event with at least one `data` line is dispatched even without a trailing empty line.
6. Limits: a line longer than MAX_LINE_BYTES or an event whose data exceeds MAX_EVENT_BYTES is a stream failure (F-LIMIT). The parser MUST NOT buffer more than one event plus one partial line.

### 2.2 Payload handling (MUST)

1. Trim ASCII whitespace from the joined data. Empty -> ignored.
2. Exactly `[DONE]` (case-sensitive) -> the end sentinel (event DONE).
3. Otherwise parse as JSON with depth limit MAX_JSON_DEPTH. A payload that is not parseable, or whose root is not a JSON object (arrays, strings, numbers, booleans, null), MUST be ignored (debug log, no frame, no error). It does not start the message.
4. Classification of a parsed object, in order:
   a. ERRCHUNK if the SSE event name is `error`, or the object has a top-level member `error` whose value is not null, not an empty string and not an empty object, or its `type` equals `error`.
   b. otherwise CHUNK (any other event name, including `message`, is irrelevant).

### 2.3 Chunk interpretation (MUST)

For a CHUNK:
- Choice selection: if `choices` is a non-empty array, use the element whose `index` is 0 if one exists, else the first element. All other elements are ignored. Empty, absent or non-array `choices` -> no choice.
- Delta source: the choice's `delta` object; if there is none, the choice's `message` object is read as if it were the delta.
- Text pieces (in this order): `content` as a string; or, when `content` is an array, the concatenation, with no separator, of its string elements and of the `text` strings of its object elements (typed `text` or untyped); then `delta.refusal` when it is a non-empty string. Each non-empty result is one text piece. Any other `content` shape is ignored.
- Reasoning pieces: section 6.
- Tool entries: `delta.tool_calls` when it is an array; other shapes are ignored. Non-object elements are skipped.
- `finish_reason`: the choice's `finish_reason` when it is a non-empty string (lower-cased for comparisons); `null`, empty or other types are ignored.
- Usage: the top-level `usage` object, else the choice's `usage` object (section 7.1).
- Everything else (`id`, `model`, `created`, `object`, `system_fingerprint`, `role`, `logprobs`, `audio`, `function_call`, annotations, unknown keys) MUST be ignored. Upstream `id` and `model` are never exposed (D2, D3).

---

## 3. Output frames (exact shapes)

### 3.1 Framing

Each frame is `event: <type>` LF `data: <compact JSON>` LF LF. The event name equals the JSON `type`. Compact JSON, UTF-8 not escaped to `\u` form unless required, one line. No `id:` or `retry:` lines. Keys in the order shown.

### 3.2 Frame catalogue

| Frame | Data |
|---|---|
| message_start | `{"type":"message_start","message":{"id":"msg_<24>","type":"message","role":"assistant","model":<inboundModel>,"content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":E,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":1}}}` |
| ping | `{"type":"ping"}` |
| text block start | `{"type":"content_block_start","index":I,"content_block":{"type":"text","text":""}}` |
| thinking block start | `{"type":"content_block_start","index":I,"content_block":{"type":"thinking","thinking":"","signature":""}}` |
| tool_use block start | `{"type":"content_block_start","index":I,"content_block":{"type":"tool_use","id":ID,"name":NAME,"input":{}}}` |
| text delta | `{"type":"content_block_delta","index":I,"delta":{"type":"text_delta","text":S}}` |
| thinking delta | `{"type":"content_block_delta","index":I,"delta":{"type":"thinking_delta","thinking":S}}` |
| tool input delta | `{"type":"content_block_delta","index":I,"delta":{"type":"input_json_delta","partial_json":J}}` |
| block stop | `{"type":"content_block_stop","index":I}` |
| message_delta | `{"type":"message_delta","delta":{"stop_reason":R,"stop_sequence":null},"usage":{"input_tokens":N,"cache_creation_input_tokens":N,"cache_read_input_tokens":N,"output_tokens":N}}` |
| message_stop | `{"type":"message_stop"}` |
| error | `{"type":"error","error":{"type":T,"message":M}}` |

- `msg_<24>`: `msg_` + SYNTH_ID_CHARS random base62 characters, unique per response (D3). `model` is the inbound model verbatim (D2).
- The thinking block start always carries `"signature":""`; no `signature_delta` frame is ever written (D7).
- message_delta `usage` MUST always contain all four counters (D8; never empty).
- A tool_use block is always exactly three frames written back to back: start, ONE `input_json_delta`, stop (section 5). `partial_json` is the validated compact JSON object text (`{}` for no arguments).
- Text and thinking deltas are written one per upstream piece, in arrival order, never merged across pieces, never reordered, never trimmed (D12).

### 3.3 Sequence and invariants (MUST)

1. The first frame of a normal stream is message_start; the second is ping (D9). The only exception is a failure before the message starts (a lone error frame).
2. After message_start: zero or more blocks, then exactly one message_delta, then exactly one message_stop. Nothing follows message_stop.
3. Block indexes are dense from 0 and strictly increasing in order of block start.
4. Blocks are strictly sequential: a block is stopped before the next block starts. At most one block is open at any time.
5. Every started block receives exactly one stop before message_delta (on normal completion).
6. No delta is ever written to a stopped block; a stopped block is never reopened.
7. A block type switch (thinking -> text -> tool_use, in any order) closes the previous block first.
8. After an error frame nothing further is written (no message_delta, no message_stop); open blocks are left unclosed.
9. Ping frames MAY appear anywhere after message_start; they never interrupt the three-frame tool_use burst.

---

## 4. State machine

### 4.1 Per-stream variables

| Variable | Initial | Meaning |
|---|---|---|
| started | false | message_start written |
| terminal | none | none / done / failed / dead |
| msgId | synthesized at start | `msg_<24>` |
| nextIdx | 0 | next block index |
| openKind, openIdx | none | the one open text-or-thinking block |
| calls | empty ordered list | call records (5.1); each: orderNo, upstreamIndex?, id?, name?, args (bounded buffer), argsBytes, balanced (incremental), state pending/emitted/dropped |
| holdMode | false | true once calls can no longer complete by index change (5.3) |
| emittedToolUse | 0 | number of tool_use blocks written |
| emittedTextBlocks | 0 | number of text blocks written |
| usedIds | empty set | tool_use ids written in this message (D5) |
| finishReason | unset | last non-empty finish_reason |
| finishSeen | false | a finish_reason has been seen |
| finishDeadline | unset | time of first finish_reason + FINISH_GRACE |
| usage | all unset | merged counters: prompt, completion, total, cacheRead, cacheWrite (7.1) |
| usageAfterFinish | false | a usage object with at least one recognized non-placeholder counter was processed at or after the finish_reason |
| outputEstimateState | zero | running counts the estimator needs over emitted text, thinking and tool_use input (no text is retained) |
| lastWriteAt | now | time of the last frame written (resets PING_IDLE) |

Memory is bounded by the MAX_* constants; text and thinking content is never retained.

### 4.2 Derived states

| State | Condition |
|---|---|
| WAIT_FIRST | not started |
| IDLE_BLOCKS | started, no open block, no pending call |
| TEXT | openKind = text |
| THINK | openKind = thinking |
| CALLS | at least one pending call (no open block by invariant) |
| LATCHED | finishSeen (orthogonal flag on any started state) |
| DONE | message_stop written |
| FAILED | error frame written |
| DEAD | client aborted; nothing more is written |

Invariant: a pending call and an open text/thinking block never coexist (the first entry of a new call closes the open block; section 5.4).

### 4.3 Primitive actions

- START: if not started: write message_start, then ping; started = true. Starting also happens by idleness (4.5).
- WRITE(frame): write and flush; lastWriteAt = now.
- CLOSE_BLOCK: if a block is open: write block stop for openIdx; openKind = none.
- TEXT(piece) / REASONING(piece): see 4.4 step 3/4.
- TOOL_ENTRY(entry): section 5.
- COMPLETE_ALL(trigger): for every pending call in ascending (upstreamIndex, orderNo) order, run EMIT_CALL (5.5). trigger is content, finish or end. `lengthCut` = trigger is finish or end AND finishReason is length-class (7.3).
- FINALIZE: if terminal != none return; COMPLETE_ALL(end); CLOSE_BLOCK; compute stop_reason (7.3) and usage (7.2); WRITE message_delta; WRITE message_stop; terminal = done; stop reading; dispose the upstream response.
- FAIL(errorType, message): if terminal != none return; WRITE the error frame (8.2); terminal = failed; stop reading; dispose the upstream response. Nothing else is written.

### 4.4 Per-CHUNK procedure (order is normative)

On CHUNK:
1. If not started: START.
2. Interpret the chunk (2.3).
3. For each reasoning piece (6): if any call is pending, COMPLETE_ALL(content). If openKind != thinking: CLOSE_BLOCK, open a thinking block (index nextIdx++, write thinking start). Write the thinking delta.
4. For each text piece: if any call is pending, COMPLETE_ALL(content). If openKind != text: CLOSE_BLOCK, open a text block (index nextIdx++, write text start). Write the text delta. A whitespace-only piece is an ordinary piece (D12).
5. For each tool entry in array order: TOOL_ENTRY.
6. If the chunk has a non-empty finish_reason: finishReason = it; if not finishSeen then finishSeen = true and finishDeadline = now + FINISH_GRACE; COMPLETE_ALL(finish).
7. Merge the chunk's usage (7.1). If finishSeen and the merged-in usage object carried at least one recognized non-placeholder counter in this chunk: usageAfterFinish = true.
8. If finishSeen and usageAfterFinish: FINALIZE.

Consequences: content that arrives in the same chunk as the finish_reason is emitted before the finish is processed; content that arrives after the finish_reason (before FINALIZE) is emitted normally; chunks arriving after FINALIZE are never read.

### 4.5 Transition table

| State | Event | Actions | Next |
|---|---|---|---|
| WAIT_FIRST | CHUNK | per-chunk procedure (starts with START) | per result |
| WAIT_FIRST | PING_IDLE elapsed since the upstream 2xx with no CHUNK | START (message_start, ping) | IDLE_BLOCKS |
| WAIT_FIRST | ERRCHUNK | FAIL (only the error frame is written) | FAILED |
| WAIT_FIRST | DONE | START; FINALIZE | DONE |
| WAIT_FIRST | EOF | FAIL(api_error, "Upstream stream ended before completion") | FAILED |
| started, non-terminal | CHUNK | per-chunk procedure | per result |
| started, non-terminal | PING_IDLE elapsed (no frame written) | WRITE ping | unchanged |
| started, non-terminal | NOISE (comment, malformed or non-object JSON, empty data) | none | unchanged |
| started, non-terminal | ERRCHUNK | FAIL(mapped type, upstream message) per 8.3 | FAILED |
| started, non-terminal | DONE | FINALIZE (a missing finish_reason is not an error) | DONE or FAILED |
| started, non-terminal | EOF, finishSeen | FINALIZE | DONE or FAILED |
| started, non-terminal | EOF, not finishSeen | FAIL(api_error, "Upstream stream ended before completion"); pending calls are not emitted | FAILED |
| LATCHED, non-terminal | FINISH_GRACE elapsed | FINALIZE | DONE or FAILED |
| any non-terminal | transport error or forwarder idle timeout | FAIL(api_error, ...) per 8.1 | FAILED |
| any non-terminal | MAX_* limit exceeded | FAIL(api_error, ...) per 8.1 | FAILED |
| any non-terminal | client abort | cancel the upstream read, dispose the response, write nothing | DEAD |
| DONE / FAILED / DEAD | anything | ignored | unchanged |

Timers: one idle timer (PING_IDLE since lastWriteAt, or since the upstream 2xx before the first write) and one grace timer (finishDeadline). Timer actions and event handling MUST be serialized with frame writes (single writer).

The idle rule in WAIT_FIRST means message_start is written by the first upstream chunk or after PING_IDLE of upstream silence, whichever comes first (D9). Because message_start needs only E, the inbound model and a synthesized id, starting early loses nothing.

---

## 5. Tool calls

### 5.1 Records and identity

A call record has: orderNo (arrival order), upstreamIndex (if given), id (first non-empty string wins), name (first non-empty string of `function.name` or `name`, at most 64 characters, wins; later different values are ignored), the held argument text, and `balanced`.

Arguments of an entry: `function.arguments` if that key exists on `function`, else a top-level `arguments`. A string is a fragment as is. A JSON object or array is serialized to compact text and treated as one fragment. null/absent/empty contribute nothing.

An entry with no id, no name and no argument text (for example `{"index":0}`) MUST be ignored entirely: it starts no call and closes no block.

Entry-to-call resolution (MUST):
1. Candidate C: if the entry has a numeric `index` (finite number; fractions truncated toward zero, negatives raised to 0; non-numeric values count as absent), C = the most recent call with that upstream index; otherwise C = the most recent call overall.
2. The entry starts a NEW call when any of these holds: there is no candidate; the entry's id is non-empty, C has an id, and they differ; the entry has a non-empty name, C has a name, and (the names differ, or C is already emitted/dropped, or C's args are non-empty and balanced), unless the entry's id equals C's id.
3. Otherwise the entry is a continuation of C: its argument fragment is appended and a missing id/name of C is filled. A continuation fragment for a call that is already emitted or dropped is a late fragment: it MUST be discarded (warning log, counter) and never reopens or duplicates a block.
4. A new call gets orderNo = next, and is registered under its upstream index if it has one.

`balanced`: tracked incrementally while fragments are appended: the held text, ignoring whitespace, is non-empty, is not inside a string literal, and forms one complete top-level JSON value as far as bracket and string tracking can tell (all opened containers closed). Empty text is not balanced.

### 5.2 Holding (D4)

Argument text is held per call and never emitted incrementally. While held, the stream emits only pings (D9). Held bytes are bounded by MAX_TOOL_ARGS_BYTES per call and MAX_PENDING_BYTES in total; exceeding either is F-LIMIT. More than MAX_TOOL_CALLS calls in one message is F-LIMIT.

### 5.3 Completion (D4)

A pending call completes (and is emitted or dropped, 5.5) when the first of these occurs:
- a different call starts (rule 5.1.2) and the previous pending call's args are balanced (non-empty complete value); if any pending call's args are empty or not balanced at that moment, the bridge enters holdMode for the rest of the message: no call completes by index change any more and all pending calls complete together at the next content/finish/end trigger (this makes interleaved argument streaming lossless and keeps emission in ascending index order);
- a text or reasoning piece arrives (trigger content), or
- finish_reason arrives (trigger finish), or the stream ends via DONE/EOF/grace (trigger end).

Calls are always emitted one after another, in ascending (upstreamIndex, orderNo) order, each as a complete three-frame burst, so blocks never overlap.

### 5.4 Text and reasoning after tool calls

- The first entry of a NEW call closes the open text/thinking block immediately (block type switch). The call itself is not written yet.
- A non-empty text or reasoning piece arriving while calls are pending first completes and emits all pending calls (trigger content), then opens a new text/thinking block at the next index. Fragments that arrive afterwards for those calls are late (5.1.3) and discarded.
- Consequently a Chat stream that interleaves text after tool calls produces block order: earlier blocks, each tool_use as it completes, then the later text block.

### 5.5 EMIT_CALL(call, trigger)

1. Name: if the call has no name: drop it (warning). If it is the only call seen in the message AND no text block has been emitted, FAIL(api_error, "Upstream returned a tool call without a name") (D4); otherwise continue.
2. Arguments (D4), applied to the held text with surrounding whitespace removed:
   - If `lengthCut` (the call was still pending when the stream ended by the length limit): empty text -> drop the call; text that strictly parses to a JSON object -> use it compacted; anything else -> drop the call. No repair is attempted. A dropped call writes nothing (warning log).
   - Otherwise: empty -> `{}`; strictly parses to a JSON object -> use it compacted; otherwise (invalid JSON, or valid JSON that is not an object) make ONE repair attempt (5.6): if the repaired text parses to an object use it compacted, else FAIL(api_error, "Upstream returned invalid arguments for tool call <name>"). `{}` is never substituted for non-empty broken arguments.
3. Id (D5): keep the upstream id if it matches `^[a-zA-Z0-9_-]{1,64}$` and is not in usedIds; otherwise synthesize `toolu_` + SYNTH_ID_CHARS random base62 characters. Add the final id to usedIds.
4. Name (D6): map the upstream name through the reverse of `toolNameMap`; a name not in the map is used unchanged.
5. Write, back to back: tool_use block start (index nextIdx++, id, name, `input:{}`), one `input_json_delta` carrying the compact JSON text, block stop. emittedToolUse++; add the text to outputEstimateState; mark the call emitted.

The block start is written only here, i.e. only after the call is complete and valid, so a dropped or failed call never leaves a started block (this satisfies D4's "only when id and name are known or the call completes").

Risk: the client sees no tool name or input while a long call is being generated (pings only); acceptable for correctness of drop-on-length and validation.

### 5.6 Compaction and repair (MUST)

- Compaction removes insignificant whitespace outside string literals. String literals (with their escapes) and number lexemes are copied unchanged (no numeric round trip); member order and duplicate keys are preserved.
- Repair, one attempt, bounded by MAX_TOOL_ARGS_BYTES and MAX_JSON_DEPTH: (1) if the text ends inside a string literal, remove an incomplete trailing escape sequence (a lone backslash or a partial `\uXXXX`) if present and append `"`; (2) remove a single dangling trailing `,` (ignoring trailing whitespace); (3) append the closing `}`/`]` required by the still-open containers, innermost first; (4) strict-parse; accept only if the result is a JSON object. No other edit is permitted: a dangling `:`, a key without a value, a partial literal or number such as `tru` or `1.`, a mismatched bracket, or nesting deeper than MAX_JSON_DEPTH make the repair fail. Keys and values are never invented. (Same rule as 02 section 7.4, which is authoritative for tool-argument repair.)

---

## 6. Reasoning

1. Sources per chunk, evaluated in this order: (a) each element of `reasoning_details` (array) in array order: a non-empty string element is a piece; an object element contributes the first non-empty string of `text`, `reasoning`, `thinking`, `summary` (summary and text pieces are treated alike); elements that only carry encrypted/opaque data (`encrypted_content`, `data`) contribute nothing; (b) the first non-empty string of `delta.reasoning_content`, `delta.reasoning`, `delta.thinking`, used only when (a) produced no piece in this chunk.
2. Each piece is written as a `thinking_delta` into the open thinking block; one thinking block per reasoning run, where a run is a maximal sequence of reasoning pieces with no text piece or tool-call completion between them. A thinking block is opened lazily by the first non-empty piece (start frame with empty `thinking` and `"signature":""`) and closed by the next block type switch, by a call completing, or by FINALIZE.
3. Reasoning that arrives after text (or after a tool_use) opens a NEW thinking block; there is no ordering restriction. Block order is the arrival order of thinking, text and tool_use runs.
4. Encrypted/redacted reasoning is dropped; `<think>` tags inside text are not interpreted (they stay literal text); `signature_delta` is never written; no `redacted_thinking` block is written (D7).
5. Empty strings never open a block.

---

## 7. Usage and stop_reason

### 7.1 Merging usage

A usage object is recognized when it is a JSON object (top-level `usage`, else the choice's `usage`). Counters are read only when they are finite JSON numbers (strings, null, booleans are ignored); each field is overwritten individually, last value wins.

| Counter | Sources, first present wins |
|---|---|
| prompt | `prompt_tokens`, `input_tokens` |
| completion | `completion_tokens`, `output_tokens` |
| total | `total_tokens` |
| cacheRead | `prompt_tokens_details.cached_tokens`, `input_tokens_details.cached_tokens`, `cached_tokens`, `cache_read_input_tokens`, `cache_read_tokens`, then (only when none of the preceding is present) the vendor alias `prompt_cache_hit_tokens` |
| cacheWrite | `prompt_tokens_details.cache_write_tokens`, `prompt_tokens_details.cache_creation_tokens`, `input_tokens_details.cache_write_tokens`, `cache_creation_input_tokens`, `cache_creation_tokens`, `cache_write_tokens` |

Placeholders: an object with no recognized numeric counter (`{}`, nulls) and an object whose recognized prompt, completion and total counters are all 0 are placeholders: they MUST NOT set `usageAfterFinish` and MUST NOT overwrite previously merged non-zero counters. A reported prompt of 0 is treated as unreported.

### 7.2 Usage in frames (D8)

- message_start: always `input_tokens` = E, `output_tokens` = 1, both cache counters 0, regardless of any usage in the first chunk.
- message_delta (written at FINALIZE), all four counters always present:
  - `cache_read_input_tokens` = cacheRead when prompt is reported, else 0.
  - `input_tokens` = max(0, prompt - cacheRead - cacheWrite) when prompt is reported; otherwise E.
  - `cache_creation_input_tokens` = cacheWrite when reported, else 0; it is subtracted from `input_tokens` together with `cacheRead` (D8, identical to 02).
  - `output_tokens`: let c = completion if reported; else max(0, total - prompt) if prompt and total are both reported; else unreported. If c is unreported, or c is 0 while any content was emitted: max(1, estimator over all emitted text, thinking and tool_use input text), using the estimator defined in 04. Otherwise c (a reported 0 with no content emitted stays 0).
- Estimates are never labelled as estimates on the wire. Empty usage is never emitted.

Risk: subtracting a reported cache-write count assumes the upstream's prompt counter includes those tokens, as OpenAI's does; an upstream that reports them outside the prompt counter would understate `input_tokens` by that amount.

### 7.3 stop_reason (D1)

`lengthClass(finishReason)`: the lower-cased value contains `length`, `max_token` or `max_output`.
- emittedToolUse >= 1 -> `tool_use` (whatever the finish_reason, including length: a call cut off by length was dropped, surviving complete calls count).
- else lengthClass -> `max_tokens`.
- else (`stop`, `content_filter`, `function_call`, unknown, absent, `tool_calls` with no surviving call) -> `end_turn`.
`stop_sequence` is always null. The `refusal` stop reason is not used.

Risk: when a complete tool_use survives a length stop, the client sees `tool_use` and is not told that later output was truncated.

---

## 8. Failure and termination

### 8.1 Mid-stream failures (frame: error)

| Failure | Type | Message |
|---|---|---|
| F-TRANSPORT: connection reset, TLS failure, premature chunked end | api_error | `Upstream connection lost` |
| F-TIMEOUT: forwarder-reported idle/overall timeout | api_error | `Upstream timed out` |
| F-EOF: EOF without finish_reason and without `[DONE]` | api_error | `Upstream stream ended before completion` |
| F-LIMIT: any MAX_* exceeded | api_error | `Upstream event exceeded the size limit` / `Upstream tool call exceeded the size limit` / `Upstream returned too many tool calls` |
| F-ARGS: invalid tool arguments outside a length cut (5.5) | api_error | `Upstream returned invalid arguments for tool call <name>` |
| F-NAME: nameless only output (5.5) | api_error | `Upstream returned a tool call without a name` |

EOF after a finish_reason without `[DONE]` is a clean end (FINALIZE). `[DONE]` without a finish_reason is a clean end (D1 gives `tool_use` if a tool_use was emitted, else `end_turn`). A forwarder timeout is never replaced by a bridge-side deadline shorter than the forwarder's.

Risk: the CLI is known to retry streamed failures reliably only for overloaded-class errors; api_error frames for transient truncation are surfaced to the user instead of being silently retried (D10 fixes api_error).

### 8.2 Error frame (D10)

Envelope `{"type":"error","error":{"type":T,"message":M}}`. M is at most ERROR_MESSAGE_MAX_CHARS. After the frame the response ends; no message_delta or message_stop follows; open blocks stay unclosed; held calls are not emitted. If nothing was written yet, the error frame is the only frame.

### 8.3 In-band error objects

An ERRCHUNK (2.2.4a) becomes an error frame:
- Message: `error.message`, else top-level `message`, else `detail`, else the `error` value if it is a string; if none, `Upstream reported an error`.
- Status S: the first numeric (or numeric-string) value in 400-599 among `error.status`, `error.http_status`, `error.code`, top-level `status`, top-level `code`. If S exists, type per the D10 status table (400 invalid_request_error, 401 authentication_error, 403 permission_error, 404 not_found_error, 413 request_too_large, 429 rate_limit_error, 408/504 timeout_error, 500 api_error, 502 api_error, 503/529 overloaded_error, other 4xx invalid_request_error, other 5xx api_error).
- Otherwise by the lower-cased string in `error.type` or `error.code`: `rate_limit_error`, `rate_limit_exceeded`, `insufficient_quota`, `too_many_requests` -> rate_limit_error; `overloaded_error`, `overloaded`, `server_overloaded`, `service_unavailable` -> overloaded_error; `authentication_error`, `invalid_api_key`, `unauthorized` -> authentication_error; `permission_error`, `permission_denied`, `forbidden` -> permission_error; `not_found_error`, `model_not_found`, `not_found` -> not_found_error; `request_too_large` -> request_too_large; `timeout`, `timeout_error` -> timeout_error; `invalid_request_error` or any value starting `invalid_` -> invalid_request_error; anything else -> api_error.
- Context-length overflow (code `context_length_exceeded`, or the message matches "maximum context length", "prompt is too long" or "exceeds the context window"): type invalid_request_error and a message starting with `prompt is too long` (exact text per 04).

---

## 9. Vendor quirks (all MUST be handled as stated)

| Quirk | Handling |
|---|---|
| Role-only first chunk (`delta:{role:"assistant",content:""}`) | START only; no block |
| `content:null`, `content:""`, `delta:{}` | no piece, no block |
| Empty `choices` chunk (prompt-filter annotations) as first or later chunk | START if first; otherwise nothing |
| Usage-only chunk (`choices:[]` + `usage`) | merge usage; finalize only if a finish_reason was seen |
| `usage:{}` / all-zero usage placeholder on any chunk | not usage (7.1); never finalizes |
| `usage:null` on every non-final chunk | ignored |
| finish_reason chunk before the usage chunk | finish latched; wait for usage, DONE, EOF or FINISH_GRACE |
| usage in the same chunk as finish_reason | finalize at once (after the chunk's content) |
| usage before the finish_reason and none after | finalize on DONE, EOF or FINISH_GRACE using the earlier usage |
| usage nested in `choices[0].usage` | accepted |
| finish_reason chunk that also carries a content delta | content emitted first, then the finish is processed |
| `finish_reason:"stop"` together with tool calls | stop_reason `tool_use` (D1) |
| Tool call arriving whole in one chunk (id, name, full arguments) | held; emitted at completion like any call |
| Missing `index`, or one `index` reused for different calls | identity rules 5.1 (id/name based), never merged blindly |
| Tool call id or name arriving after its first arguments | the call stays pending until both are known or it completes |
| Arguments as an object instead of a string | serialized, treated as one fragment |
| `tool_calls:[]` or `null` on content chunks | ignored |
| Reasoning in `reasoning_content`, `reasoning`, `thinking` or `reasoning_details` | section 6 |
| `message` object instead of `delta` | read as the delta |
| `content` as an array of parts, `refusal` text | section 2.3 |
| `[DONE]` missing but connection closed after finish_reason | clean end |
| Upstream comments / blank keep-alives | ignored (liveness only) |
| `n > 1` style extra choices | ignored |

---

## 10. Edge-case matrix (expected values are the TARGET behavior)

### 10.1 Notation

Context: inbound model `M`; request estimate `E`; `EST` = estimator result over emitted output (minimum 1). Every row's `MS;PING` is written by the first upstream chunk (or by DONE) unless noted.

Input chunks: `R` role-only first chunk (`delta:{role:"assistant",content:""}`); `T"x"` content delta; `K"x"` `reasoning_content` delta; `TC[idx/id/name/args]` one tool entry (`-` = key absent, `""` = empty string; `idx` `-` = no index); `F:x` chunk with empty delta and finish_reason x; `U(p,c)` chunk with `choices:[]` and usage prompt p, completion c (`cr=n` adds cached_tokens n); `+` = in the same chunk; `DONE`, `EOF`, `RST` (connection reset), `COMMENT`.

Output frames: `MS`, `PING`, `B+(i,text)`, `B+(i,think)` (always with `"signature":""`), `Dt(i,"s")`, `Dk(i,"s")`, `B-(i)`, `TU(i,id,name,json)` = the three-frame burst `B+(i,tool id name)`; `Dj(i,json)`; `B-(i)`, `MD{stop;in;cc;cr;out}`, `MX`, `ERR{type,"msg"}`. `n:` prefixes give the output for the n-th input item; `//` separates items; `(none)` = no frame. `X` = a synthesized `toolu_<24>` id.

### 10.2 Lifecycle and termination

| ID | Input | Expected |
|---|---|---|
| L01 | R, T"Hi", T" yo", F:stop, U(10,2) | 1: MS;PING // 2: B+(0,text);Dt(0,"Hi") // 3: Dt(0," yo") // 4: (none) // 5: B-(0);MD{end_turn;in10;cc0;cr0;out2};MX |
| L02 | R, T"a", [T"b"+F:stop+U(5,2)] | 3: Dt(0,"b");B-(0);MD{end_turn;in5;cc0;cr0;out2};MX |
| L03 | R, T"a", U(5,1), F:stop, DONE | 3: (none) // 4: (none) // 5: B-(0);MD{end_turn;in5;cc0;cr0;out1};MX (usage preceded the finish; wait for DONE) |
| L04 | R, T"a", U(5,1), F:stop, then silence | 2 s after F:stop (FINISH_GRACE): B-(0);MD{end_turn;in5;cc0;cr0;out1};MX |
| L05 | R, T"Hi", F:stop, DONE | 4: B-(0);MD{end_turn;inE;cc0;cr0;outEST};MX |
| L06 | R, T"Hi", F:stop, EOF | at EOF: same as L05 |
| L07 | R, T"Hi", DONE (no finish_reason) | at DONE: B-(0);MD{end_turn;inE;cc0;cr0;outEST};MX |
| L08 | R, T"Hi", EOF | 3: ERR{api_error,"Upstream stream ended before completion"}; block 0 left open; no MD/MX |
| L09 | EOF only | 1: ERR{api_error,"Upstream stream ended before completion"} (no MS) |
| L10 | DONE only | 1: MS;PING;MD{end_turn;inE;cc0;cr0;out1};MX (empty content, not an error) |
| L11 | R, [F:stop+U(7,0)] | 2: MD{end_turn;in7;cc0;cr0;out0};MX (no blocks) |
| L12 | R, content null, T"", [F:stop+U(3,0)] | no blocks; MD{end_turn;in3;cc0;cr0;out0};MX |
| L13 | R, T"a", [F:stop+usage {}], U(3,1) | 3: (none) // 4: B-(0);MD{end_turn;in3;cc0;cr0;out1};MX |
| L14 | R, T"a", [F:stop+usage {0,0,0}], DONE | 3: (none) // 4: B-(0);MD{end_turn;inE;cc0;cr0;outEST};MX |
| L15 | usage given as a string `"x"` | ignored as usage |
| L16 | R, [T"z"+F:stop], U(4,1) | 2: B+(0,text);Dt(0,"z") // 3: B-(0);MD{end_turn;in4;cc0;cr0;out1};MX |
| L17 | R, T"a", F:stop, T"more", U(1,1) | 3: (none) // 4: Dt(0,"more") // 5: B-(0);MD{end_turn;in1;cc0;cr0;out1};MX |
| L18 | R, TC[0/c1/f/{}], F:tool_calls, F:stop, U(2,1) | 3: TU(0,c1,f,{}) // 4: (none) // 5: MD{tool_use;in2;cc0;cr0;out1};MX |
| L19 | R, T"a", [F:stop+U(5,1)], U(9,9) | 3 finalizes (MD out1 in5); item 4 is never read |
| L20 | R, T"a", F:stop, then usage 3 s later | at +2 s: B-(0);MD{end_turn;inE;cc0;cr0;outEST};MX; the late usage is never read |
| L21 | upstream 2xx, silence 8 s, silence 8 s, then R | +8 s: MS;PING // +16 s: PING // R: (none; already started) |
| L22 | R, T"a", silence 8 s | +8 s: PING (repeats every 8 s of silence) |
| L23 | R, TC[0/c1/f/""], TC[0/-/-/'{"a"'], silence 20 s | PING at +8 s and +16 s; no other frame |
| L24 | COMMENT, R | COMMENT: (none; message not started) // R: MS;PING |
| L25 | first chunk `choices:[]` without usage | MS;PING |
| L26 | first chunk `choices:[]` with usage, then T"a", F:stop, DONE | MS;PING; usage merged; at DONE: B-(0);MD{end_turn;...} using the merged usage |
| L27 | JSON array line, then R | array ignored (no MS); R: MS;PING |
| L28 | `data: junk`, truncated JSON, then R | ignored; R: MS;PING |
| L29 | first chunk id `chatcmpl-9`, model `gpt-4o` | message_start id `msg_<24>`, model `M`; upstream id/model never appear |

### 10.3 Text

| ID | Input | Expected |
|---|---|---|
| T01 | R, T"\n" | B+(0,text);Dt(0,"\n") (whitespace preserved) |
| T02 | content `[{"type":"text","text":"a"},{"type":"text","text":"b"}]` | one Dt(0,"ab") |
| T03 | content as an object or number | ignored |
| T04 | refusal "No." | B+(0,text);Dt(0,"No.") |
| T05 | content "x" + refusal "y" in one chunk | Dt(0,"x") then Dt(0,"y") |
| T06 | a 4-byte character split across two network reads | one Dt containing the complete character |
| T07 | `{message:{content:"hi"}}` instead of delta | B+(0,text);Dt(0,"hi") |
| T08 | choices `[{index:1,...},{index:0,delta:{content:"x"}}]` | the index-0 element is used: Dt(0,"x") |
| T09 | choices `[{index:1,delta:{content:"x"}}]` only | that element is used (no index-0 element exists) |
| T10 | text, finish in the same chunk as the last text | text frames first, then finish handling |

### 10.4 Reasoning

| ID | Input | Expected |
|---|---|---|
| K01 | R, K"Hm", K" ok", T"Ans", F:stop, U(3,4) | 2: B+(0,think);Dk(0,"Hm") // 3: Dk(0," ok") // 4: B-(0);B+(1,text);Dt(1,"Ans") // 5: (none) // 6: B-(1);MD{end_turn;in3;cc0;cr0;out4};MX |
| K02 | `reasoning` field instead of `reasoning_content` | same as K01 |
| K03 | `thinking` field | same as K01 |
| K04 | reasoning_details `[{type:"reasoning.summary",summary:"S"},{type:"reasoning.text",text:"T"}]` | Dk(0,"S");Dk(0,"T") in one block, array order |
| K05 | details text "X" and reasoning_content "X" in one chunk | one Dk(0,"X") (scalar ignored when details produced a piece) |
| K06 | details only `[{type:"reasoning.encrypted",data:"abc"}]` | no block |
| K07 | encrypted details + `reasoning_content:"r"` | Dk(0,"r") |
| K08 | T"a", K"b" | B+(0,text);Dt;B-(0);B+(1,think);Dk(1,"b") |
| K09 | K"t", TC[0/c1/f/'{}'], F:tool_calls | B+(0,think);Dk // B-(0) at the call's first entry // at F: TU(1,c1,f,{}) |
| K10 | content `<think>x</think>y` | one Dt with the literal text |
| K11 | `reasoning_content:""` | no block |
| K12 | `{reasoning_content:"r",content:"t"}` in one chunk | B+(0,think);Dk(0,"r");B-(0);B+(1,text);Dt(1,"t") |
| K13 | K"a", T"b", K"c" | think 0, text 1, think 2 (one block per run) |

### 10.5 Tool calls

| ID | Input | Expected |
|---|---|---|
| C01 | R, TC[0/call_1/get_weather/""], TC[0/-/-/'{"ci'], TC[0/-/-/'ty":"X"}'], F:tool_calls, U(20,5) | 2-4: (none) // 5: TU(0,call_1,get_weather,'{"city":"X"}') // 6: MD{tool_use;in20;cc0;cr0;out5};MX |
| C02 | T"ok", TC[0/c1/f/'{}'], [F:tool_calls+U(1,1)] | 1: MS;PING;B+(0,text);Dt(0,"ok") // 2: B-(0) // 3: TU(1,c1,f,{});MD{tool_use;in1;cc0;cr0;out1};MX |
| C03 | whole call in one chunk TC[0/c1/f/'{"a":1}'], then F:stop, U(2,1) | held; F: TU(0,c1,f,'{"a":1}'); stop_reason tool_use |
| C04 | TC[0/a/f/""], TC[0/-/-/'{"x":1}'], TC[1/b/g/""], TC[1/-/-/'{"y":2}'], [F:tool_calls+U(9,3)] | item 3: TU(0,a,f,'{"x":1}') (index 1 started, call 0 balanced) // item 5: TU(1,b,g,'{"y":2}');MD{tool_use;in9;cc0;cr0;out3};MX |
| C05 | interleaved: TC[0/a/f/""], TC[1/b/g/""], TC[0/-/-/'{"a"'], TC[1/-/-/'{"b"'], TC[0/-/-/':1}'], TC[1/-/-/':2}'], F:tool_calls | holdMode at item 2; nothing until F: TU(0,a,f,'{"a":1}');TU(1,b,g,'{"b":2}') |
| C06 | one chunk with entries [0/a/f/'{"x":1}', 1/b/g/'{"y":2}'], then F:tool_calls | in that chunk: TU(0,a,f,'{"x":1}'); at F: TU(1,b,g,'{"y":2}') |
| C07 | TC[0/-/-/'{"a":1}'], TC[0/call_9/f/-], F:tool_calls | at F: TU(0,call_9,f,'{"a":1}') (id/name arrived late) |
| C08 | TC[0/-/f/'{}'], F:stop, U | TU(0,X,f,{}); stop_reason tool_use |
| C09 | TC[0/c1/-/'{}'], F:tool_calls, U(1,1) (name never given, only output) | at F: ERR{api_error,"Upstream returned a tool call without a name"} |
| C10 | T"hi", TC[0/c1/-/'{}'], F:stop, U(2,1) | text block closed at the entry; at F the call is dropped (warning); MD{end_turn;...};MX |
| C11 | TC[-/c1/f/'{"p":1}'], TC[-/c2/f/'{"p":2}'], [F:stop+U(5,2)] | item 2: TU(0,c1,f,'{"p":1}') // item 3: TU(1,c2,f,'{"p":2}');MD{tool_use;in5;cc0;cr0;out2};MX |
| C12 | TC[0/c1/f/'{"p":1}'], TC[0/c2/g/'{"p":2}'] (same index, new id) | two calls, as C11 |
| C13 | TC[0/c1/f/'{"p":'], TC[0/-/-/'1}'] | one call, args `{"p":1}` |
| C14 | TC[-/-/f/'{"p":1}'], TC[-/-/f/'{"p":2}'] (no index, no id) | second entry has a name and the first call is balanced -> new call: two TU |
| C15 | TC[-/-/f/'{"p":'], TC[-/-/f/'1}'] | second entry continues (first call not balanced): one call `{"p":1}` |
| C16 | id `""` | synthesized X |
| C17 | id `call:1` (illegal char) | synthesized X |
| C18 | id of 65 characters | synthesized X |
| C19 | two calls both with id `dup` | first keeps `dup`, second gets a synthesized X |
| C20 | name `mcp__srv__do_it`, request map has `mcp__srv__do.it` | tool_use name `mcp__srv__do.it` |
| C21 | name not in the map | name unchanged |
| C22 | args `""`, `"  "`, `"{}"` | `Dj(i,"{}")` in each case |
| C23 | args `{ "a" : 1.0 , "b" : [ 1 , 2 ] , "s":"x y" }` | `{"a":1.0,"b":[1,2],"s":"x y"}` |
| C24 | args `{"n":1e3,"m":12345678901234567890}` | unchanged (number lexemes preserved) |
| C25 | args `{"a":"x` , F:tool_calls | repaired: `{"a":"x"}` |
| C26 | args `{"a":[1,2` , F:stop | repaired: `{"a":[1,2]}` |
| C27 | args `{"a":}` , F:tool_calls | ERR{api_error,"Upstream returned invalid arguments for tool call <name>"} |
| C28 | args `{"a":` , F:tool_calls | repair fails -> ERR (as C27) |
| C29 | args `{"a":1,` , F:tool_calls | repair fails -> ERR |
| C30 | args `[1]` or `null` or `"x"` , F:tool_calls | not an object, repair fails -> ERR |
| C31 | args `{"path":"/t","content":"hel` , F:length, U | call cut off by length: dropped, no block; MD{max_tokens;...};MX |
| C32 | args `{"a":1}` pending, F:length | valid as is: TU emitted; MD{tool_use;...} |
| C33 | call 0 complete `{"a":1}`, call 1 args `{"b":` , F:length | call 0 emitted at call 1's start; call 1 dropped; MD{tool_use;...} |
| C34 | name given, args `""`, F:length | dropped (cut off before arguments); MD{max_tokens;...} |
| C35 | `arguments` as an object `{"a":1}` | held as text; TU(...,'{"a":1}') |
| C36 | TC[0/c1/f/'{"a":1}'], T"done", F:stop, U(3,2) | 2: TU(0,c1,f,'{"a":1}');B+(1,text);Dt(1,"done") // 4: B-(1);MD{tool_use;in3;cc0;cr0;out2};MX |
| C37 | TC[0/c1/f/'{"a":1}'], K"hm" | 2: TU(0,...);B+(1,think);Dk(1,"hm") |
| C38 | TC[0/c1/f/'{"a":1}'], TC[1/c2/g/'{}'], TC[0/-/-/'x'] | item 2 emits call 0; the late fragment in item 3 is discarded; no frame |
| C39 | `tool_calls` `[]` or `null` | (none) |
| C40 | entry `{"index":0}` only | ignored: no call, open block stays open |
| C41 | 129th call in one message | ERR{api_error,"Upstream returned too many tool calls"} |
| C42 | args exceeding 4 MiB | ERR{api_error,"Upstream tool call exceeded the size limit"} |
| C43 | tool call in the first chunk (no role chunk) | MS;PING; call held |
| C44 | `index` given as the string "0" | treated as absent (5.1) |
| C45 | RST while a call is held | ERR{api_error,"Upstream connection lost"}; the call is never emitted |

### 10.6 stop_reason

| ID | Input | Expected |
|---|---|---|
| S01 | text, F:length, U | max_tokens |
| S02 | F:tool_calls without any tool entry | end_turn |
| S03 | F:function_call | end_turn |
| S04 | F:content_filter | end_turn |
| S05 | F:"MAX_TOKENS" | max_tokens |
| S06 | finish_reason `""` or null | ignored; end_turn at the end |
| S07 | F:"foo" | end_turn |
| S08 | F:length with a surviving complete tool_use | tool_use |
| S09 | F:stop with a tool_use emitted | tool_use |
| S10 | F:tool_calls, F:stop (two finishes), tool_use emitted | last finish wins; tool_use because a block was emitted |

### 10.7 Usage

| ID | Input | Expected |
|---|---|---|
| U01 | U(100,20;cr=60) | in40 cc0 cr60 out20 |
| U02 | prompt 100, cached 60, cache_write 10 | in30 cc10 cr60 (both cache counters subtracted) |
| U03 | cached 50 > prompt 10 | in0 |
| U04 | usage nested in `choices[0].usage` with the finish | counted; finalized in that chunk |
| U05 | only `prompt_cache_hit_tokens:30` | cr30 |
| U06 | `cached_tokens:10` and `prompt_cache_hit_tokens:30` | cr10 |
| U07 | counters as strings | ignored; estimates used |
| U08 | completion reported, prompt missing | inE, out as reported, cr0 |
| U09 | prompt reported, completion and total missing | out = EST |
| U10 | prompt 10, total 25, no completion | out15 |
| U11 | completion 0 reported but text emitted | out = EST |
| U12 | prompt_tokens 0 | inE (treated as unreported) |
| U13 | prompt in the first chunk, completion in the last | merged |
| U14 | first chunk carries usage | message_start still uses E and out 1 |
| U15 | usage with `server_tool_use` | ignored |

### 10.8 Errors and termination

| ID | Input | Expected |
|---|---|---|
| H01 | R, T"Par", `event: error` + `{"error":{"type":"overloaded_error","message":"busy"}}` | MS;PING // B+;Dt // ERR{overloaded_error,"busy"}; no MD/MX |
| H02 | bare `{"error":{"message":"rate limited","type":"rate_limit_error"}}` mid-stream | ERR{rate_limit_error,"rate limited"} |
| H03 | `{"error":"boom"}` | ERR{api_error,"boom"} |
| H04 | `{"type":"error","message":"x"}` | ERR{api_error,"x"} |
| H05 | error with numeric `code:429` | ERR{rate_limit_error,...} |
| H06 | error `code:"rate_limit_exceeded"` | ERR{rate_limit_error,...} |
| H07 | error `code:"context_length_exceeded"`, message `maximum context length is 8192` | ERR{invalid_request_error,"prompt is too long..."} |
| H08 | error object as the first chunk | 1: ERR only (no MS) |
| H09 | error with `status:503` | ERR{overloaded_error,...} |
| H10 | object with both `error` and `choices` | `choices` processed per 02 (the `error` member is ignored when `choices` is an array); in-band error only when `choices` is not an array (02 section 11.5) |
| H11 | normal chunk with `"error":null`, `"error":""` or `"error":{}` | ordinary chunk |
| H12 | R, T"Par", RST | ERR{api_error,"Upstream connection lost"} |
| H13 | forwarder reports an idle timeout | ERR{api_error,"Upstream timed out"} |
| H14 | client disconnects | nothing written; upstream cancelled |
| H15 | duplicate `[DONE]` or data after `[DONE]` | never read |
| H16 | error while a call is held | ERR; the call is not emitted |
| H17 | error message of 5,000 characters | truncated to 2,000 |
| H18 | invalid arguments (non-length) after a text block | text block already closed; ERR{api_error,"Upstream returned invalid arguments for tool call <name>"} |
| H19 | 2xx with `application/json` body for a streaming request | parsed as Chat JSON, mapped by 02, replayed as MS;PING;blocks;MD;MX |
| H20 | error `type:"server_error"` | ERR{api_error,...} |
| H21 | error `code:"insufficient_quota"` | ERR{rate_limit_error,...} |
| H22 | error with no message anywhere | ERR{...,"Upstream reported an error"} |

### 10.9 SSE parsing

| ID | Input | Expected |
|---|---|---|
| P01 | CRLF line endings | parsed normally |
| P02 | lone CR terminators | parsed normally |
| P03 | BOM before the first `data:` | first event parsed |
| P04 | two `data:` lines forming one JSON object | joined with LF, parsed |
| P05 | `data:{...}` without space | parsed |
| P06 | `data:  {...}` (two spaces) | one space removed; parsed |
| P07 | comment lines between events | ignored |
| P08 | `id:` / `retry:` lines | ignored |
| P09 | last event without a trailing empty line, then EOF | processed |
| P10 | event data above 8 MiB | ERR{api_error,"Upstream event exceeded the size limit"} |
| P11 | line above 8 MiB | same |
| P12 | `data:` with an empty payload | ignored |
| P13 | event name `message` or none | processed as a chunk |
| P14 | multi-line data that is not valid JSON | ignored |

---

## 11. Implementation requirements (.NET)

- Processing MUST be incremental and event-driven: read the upstream with response-headers-read semantics, feed bytes through the section 2 parser, write and flush frames per upstream event; never buffer the whole body.
- Memory MUST stay bounded by the section 0.2 limits; text and thinking content is never retained; only held tool-call arguments are buffered.
- JSON handling MUST NOT use reflection or runtime-generated serializers (NativeAOT): read chunks with a document/reader API and write frames with a writer API; tool-argument validation, compaction (whitespace removal without numeric round trip) and repair operate on the text.
- A single writer MUST serialize frames, pings and the final frames; timers (PING_IDLE, FINISH_GRACE) and the upstream reader MUST be coordinated so that no frame is written after a terminal state.
- The client request's abort signal MUST cancel the upstream read and the response disposal; no frames are written after abort.

---

## 12. Open items (need live-upstream measurement)
- Tool-call argument streaming: native Messages streams `input_json_delta` fragments as they arrive; this design holds each call until its arguments validate (D4), so the client sees the tool name and input only at completion. The trade-off is deliberate (validation before execution, no silent `{}`). Revisit if a real Claude Code session shows unacceptable tool UX; the trigger is a user-visible stall on a large Write/Edit call.

1. Whether any target upstream interleaves text or reasoning deltas between argument fragments of one call; the text-after-tool rule (5.4) would discard such late fragments.
2. Frequency of `null` arguments or arguments delivered as a JSON string containing JSON; both currently fail validation (D4); an unwrap step may be warranted.
3. Whether any upstream fragments tool names across chunks (first non-empty name wins) or repeats names on continuation chunks (handled by identity rule 5.1).
4. Zero-argument calls delivered with empty argument text: they put the stream into holdMode (5.3) and are emitted at finish; measure frequency and latency impact.
5. First-token latency and silent-reasoning duration per upstream, to confirm that PING_IDLE = 8 s and the forced message_start are sufficient for the CLI's own stream watchdog; the idle timeout itself is owned by the forwarder.
6. Estimator accuracy versus real counters for upstreams that omit usage despite include_usage (04 defines the estimator).
7. Upstreams that report cumulative partial usage on every chunk: finalization at the finish chunk uses the last merged values (FINISH_GRACE only matters when no usage follows the finish).
8. Whether a pre-start in-band error should become an HTTP error response when response headers are not yet committed (depends on the header commit point defined in 04); this file writes an error frame.
9. Whether FINISH_GRACE = 2 s is long enough for upstreams that send usage late.
10. Vendors that emit whitespace-only text before tool calls (a blank text block is written; D12 forbids trimming).
11. CLI behavior with thinking blocks whose signature is empty if the CLI ever forwards them to an Anthropic endpoint (01 drops thinking from history, so no round trip occurs through the bridge).
