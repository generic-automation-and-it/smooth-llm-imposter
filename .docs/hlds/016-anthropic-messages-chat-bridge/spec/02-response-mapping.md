# 02 - Response mapping: Chat Completions reply -> Messages reply (non-streaming)

Normative target specification. Keywords MUST, MUST NOT, SHOULD, MAY are used as in RFC 2119.

Siblings
- `01-request-mapping.md`: builds the Chat request; supplies the tool-name map and the request token estimate used here.
- `03-stream-state-machine.md`: streaming counterpart. Every rule in this file about ids, names, tool arguments, text/reasoning handling, stop_reason, usage and errors applies identically there; only framing differs.
- `04-harness-and-errors.md`: defines the shared token estimator, the exact context-overflow wording the client recognises, and the client-visible error contract.

Vocabulary: bridge = the component converting the Chat reply into a Messages reply; client = Claude Code; upstream = the Chat-only provider.

---

## 1. Contract

| Item | Definition |
|---|---|
| Input | Upstream HTTP status; upstream headers (only `retry-after` and `retry-after-ms` are consumed); upstream body as a string; request-derived context (below). |
| Context | (a) inbound model name exactly as the client sent it; (b) per-request tool-name map (sanitized upstream name -> original client name), built by 01; (c) request token estimate (integer), computed over the inbound request with the shared estimator defined in 04; (d) streaming flag, always false in this file. |
| Output | HTTP status; headers to forward; body string. Success: HTTP 200 and one Messages `message` JSON object. Failure: Messages error envelope (section 11). |
| Scope | Applies to every upstream reply, 2xx and non-2xx. The bridge sends a non-streaming Chat request for a non-streaming client; the reply is therefore one JSON document (see section 13 for the exception note). |
| Statelessness | The conversion MUST depend only on the input and context. No cross-request state. |

---

## 2. Processing order

The bridge MUST process a reply in this order:

1. Upstream status outside 200-299 -> error mapping (section 11). Stop.
2. Parse the body as strict JSON (RFC 8259, no comments, no trailing commas). If it is not a JSON object -> HTTP 502 `api_error` (section 11.4). Stop.
3. If the object has an `error` member that is an object or a non-empty string, and `choices` is not an array -> in-band error (section 11.5). Stop.
4. If `choices` is missing or not an array -> HTTP 502 `api_error`. Stop. (An empty array is valid: it is an empty output.)
5. Select the choice (section 4).
6. Extract text, refusal text, reasoning text and tool calls (sections 5-7).
7. If the selected choice carries an object member `error` and no text, refusal text, reasoning text or tool calls were extracted -> in-band error (section 11.5). Stop.
8. Validate tool-call arguments (section 7.4). This step may drop calls or fail the message.
9. Assemble blocks (section 8), compute `stop_reason` (section 9) and `usage` (section 10).
10. Emit the message (section 3) with HTTP 200.

Empty output is NOT an error: a reply with no text, no reasoning and no tool calls is translated faithfully (section 8.2). The bridge MUST NOT issue a second upstream request because an output was empty.

---

## 3. Message envelope

Emitted member order: `id, type, role, model, content, stop_reason, stop_sequence, usage`. No other members. Output JSON is compact (no insignificant whitespace), UTF-8, no BOM.

| Member | Value |
|---|---|
| `id` | `msg_` followed by 24 characters drawn uniformly from `[0-9A-Za-z]` using a cryptographically secure generator; unique per response. The upstream `id` MUST NOT be exposed. |
| `type` | `"message"` |
| `role` | `"assistant"` |
| `model` | The inbound model name from the context, byte for byte. The upstream's reported model MUST NOT be exposed; the value MUST NOT be `"unknown"` or empty. |
| `content` | Ordered block list (section 8). May be `[]`. |
| `stop_reason` | `end_turn`, `max_tokens` or `tool_use` (section 9). |
| `stop_sequence` | Always JSON `null`. |
| `usage` | Object with four integer members (section 10). Never empty, never null. |

Upstream members that MUST NOT appear in the output: `object`, `created`, `system_fingerprint`, `service_tier`, `choices[].logprobs`, `message.annotations`, `message.audio`, any vendor extension not named in this file. Dropped members SHOULD be logged at debug level by name only.

---

## 4. Choice selection

- The bridge MUST select the element of `choices` whose `index` member is the integer 0; if no element has `index` 0, the first element in array order. If the selected element is not a JSON object, the output is empty (section 8.2).
- All other choices MUST be ignored. (01 never requests more than one choice.)
- The message source is the selected choice's `message` object. If `message` is absent and the choice has an object `delta`, the bridge MAY use `delta` as the message; if neither exists and the choice has a string `text`, the bridge MAY treat it as `message.content`.
- `finish_reason` is read from the selected choice (string, otherwise absent).
- `usage` is read from the top-level `usage` object; if absent and the selected choice has an object `usage`, that object is used.

---

## 5. Text extraction

### 5.1 `message.content`

| Upstream `content` | Text |
|---|---|
| string | The string exactly as received. No trimming, no newline normalisation, no Unicode normalisation. |
| array | Concatenation, in order and with NO separator, of the text of every element that yields text (table below). |
| `null`, absent, any other JSON type | No text. |

Array elements:

| Element | Contribution |
|---|---|
| JSON string | the string |
| object with string member `text` and `type` not equal to `refusal` | the `text` value |
| object with `type` `refusal` and string member `refusal` (or `text`) | refusal text (section 5.2), not content text |
| anything else (`image_url`, `audio`, `input_audio`, numbers, `null`, arrays) | ignored |

### 5.2 Refusal

- Refusal text is `message.refusal` (string) and any refusal array elements, concatenated in order with no separator.
- Refusal text MUST be surfaced to the user as assistant text: the final text is the content text; if refusal text is also non-empty and content text is non-empty, the final text is content text + `"\n"` + refusal text; if content text is empty, the final text is the refusal text.
- Refusal text never changes `stop_reason` (section 9) and never causes `refusal` to be used as a stop reason.

### 5.3 Text block

- A `text` block MUST be emitted if and only if the final text has length greater than zero. Whitespace-only text is non-empty and MUST be emitted unchanged.
- At most one `text` block per message. Text MUST NOT be split around tool calls (Chat carries no position information).
- Markup such as `<think>...</think>` inside content is left untouched by default. When the per-upstream `thinkMarkupExtraction` option (01 section 2) is enabled, a leading `<think>`/`<thinking>`/`<reasoning>` span (an unterminated span runs to the end of the text) is moved into the reasoning text of section 6 and removed from the text; the option is off by default and MUST be selected by configuration, never by sniffing.

---

## 6. Reasoning -> thinking block

### 6.1 Sources

Read from the selected message:
1. `reasoning_details` (array)
2. `reasoning_content` (string)
3. `reasoning` (string)
4. `thinking` (string)

### 6.2 Extraction

Details pieces. For each element of `reasoning_details`, in order:

| Element | Result |
|---|---|
| string containing a non-whitespace character | that string is a piece |
| object whose `type` is `reasoning.encrypted` or `redacted_thinking` | skipped |
| other object | the first of its string members `text`, `thinking`, `reasoning`, `summary` that contains a non-whitespace character is a piece; if none, skipped (an object carrying only `data` or `encrypted_content` is skipped) |
| any other JSON type | skipped |

Details text D = the pieces joined with a single `"\n"`; absent if there are no pieces.

Plain text P = the first of `reasoning_content`, `reasoning`, `thinking` (in that order) that is a string containing a non-whitespace character; absent otherwise. The chosen value is kept untrimmed.

### 6.3 Merge

| D | P | Reasoning text |
|---|---|---|
| absent | absent | none |
| present | absent | D |
| absent | present | P |
| present | present | if P trimmed equals D trimmed, or equals any single piece trimmed: D; otherwise P + `"\n"` + D |

Trimming is used only for this equality test; the emitted text is never trimmed.

### 6.4 Block

- If reasoning text exists the bridge MUST emit exactly one block `{"type":"thinking","thinking":<text>,"signature":""}` (member order `type, thinking, signature`).
- `signature` MUST be the empty string for every thinking block (the bridge cannot sign). Any signature in the upstream data is discarded.
- `redacted_thinking` blocks MUST NOT be emitted. Encrypted or opaque reasoning data MUST be dropped without error.
- Thinking is emitted whenever the upstream supplies reasoning text, whether or not the request enabled thinking.

Risk: the empty signature is accepted by the client for display and is dropped from history by 01. If a session later continues against a real Messages upstream that validates signatures, history blocks with an empty signature would be rejected there; the client's behaviour on model switches is unverified.
Risk: emitting thinking blocks to a request that did not enable thinking is valid on the wire but has not been verified against every client version.

---

## 7. Tool calls -> `tool_use` blocks

### 7.1 Sources and order

- Calls are read from `message.tool_calls` when it is a non-empty array. Elements that are not objects are skipped. Array order is call order; the `index` and `type` members are ignored.
- When `tool_calls` is absent, `null` or empty, and `message.function_call` is an object (legacy shape), it MUST be treated as exactly one call using the same rules; it has no upstream id, so an id is synthesized. When both exist, `function_call` is ignored.
- A single call object where an array is expected (`tool_calls` is an object) MUST be ignored.
- Parallel calls: each surviving call yields its own block, in call order. There is no cap on the number of calls.

### 7.2 Name

- Upstream name = string `function.name`; if absent or empty, the string top-level `name`.
- The bridge MUST apply the reverse of the per-request name map: an exact (ordinal, case-sensitive) match of the upstream name against the map's sanitized names yields the original client name; a name not in the map is emitted unchanged.
- A call with no usable name is dropped (warning logged). If, after all drops, the message has no text, no thinking and no other tool_use block, the message MUST fail with HTTP 502 `api_error` (message: `Upstream returned a tool call without a name.`). Otherwise the surviving content is returned.

### 7.3 Id (applied in call order)

- Upstream id = non-empty string `id`.
- If the upstream id matches `^[a-zA-Z0-9_-]{1,64}$` and has not already been used by an earlier block of this message, it is kept.
- Otherwise (missing, empty, non-string, illegal characters, longer than 64, or duplicate within the message) the bridge MUST synthesize `toolu_` followed by 24 characters from `[0-9A-Za-z]`, cryptographically random, distinct from every other id in the message.
- Ids MUST NOT be positional (`call_0` style) and MUST NOT be derived from the upstream id by any non-deterministic mapping. Ids echoed back by the client in later requests are used as-is by 01.

### 7.4 Arguments -> `input` (validate before emitting)

Argument source: the first of `function.arguments`, `arguments`, `input` that is present and not JSON `null`; if none, the arguments are the empty string.

| Argument value | Result |
|---|---|
| JSON object (not a string) | used as the input object |
| string that is empty or only whitespace | `{}` |
| string that parses (strict) to a JSON object | that object |
| any other string, array, number or boolean | invalid -> repair attempt |

Repair (exactly one attempt, string arguments only). The bridge scans the string tracking string/escape state and a stack of open `{`/`[`. The only permitted edits are:
1. if the text ends inside a string literal, first remove an incomplete trailing escape (`\` or a partial `\u` sequence), then append `"`;
2. remove a single dangling trailing `,` (ignoring trailing whitespace);
3. append `]` / `}` to close open containers, innermost first.
The bridge MUST NOT supply a missing value after `:`, a missing key, a missing literal, or any other content. If the result is not a strict JSON object, the arguments remain invalid. Valid JSON that is not an object (array, string, number, boolean, `null` literal) is invalid and is not repairable.

Invalid arguments:
- If the selected choice's `finish_reason` is a length reason (section 9): that tool_use block MUST be dropped entirely (warning logged). Other calls are unaffected.
- Otherwise the whole message MUST fail with HTTP 502 `api_error`, message `Upstream returned invalid arguments for tool call "<name>".` (the argument text is not echoed). The bridge MUST NOT substitute `{}` for non-empty invalid arguments.

Output form: the input object is emitted compact. Numbers MUST keep their textual form from the upstream (no conversion through a floating-point type: `1.0`, `1e2` and integers beyond 2^53 are emitted unchanged). Member order is preserved. Duplicate member names: the last occurrence wins, and parsing MUST NOT fail because of duplicates. Nesting deeper than 64 levels is invalid.

Block: `{"type":"tool_use","id":<id>,"name":<name>,"input":<object>}`.

Risk: failing the whole message with 502 on unrepairable non-length arguments causes the client to retry 5xx responses with backoff; a deterministic upstream defect can then repeat several times and spend upstream tokens each time. Keep the decision; the message text lets the user see the cause after retries are exhausted.

---

## 8. Blocks

### 8.1 Order

`thinking` (0 or 1) -> `text` (0 or 1) -> `tool_use` (0..n, call order). Member orders: text `type, text`; thinking `type, thinking, signature`; tool_use `type, id, name, input`.

### 8.2 Empty output

If no block survives, `content` MUST be `[]`; `stop_reason` follows section 9 (normally `end_turn`, or `max_tokens` for a length reason); `usage` follows section 10 (real or estimated). This covers: empty or null content with no calls, `choices: []`, a non-object selected choice, a message whose only calls were dropped under the length rule of 7.4, and encrypted-only reasoning. (A message whose only call is nameless fails per 7.2 instead.)

Risk: a message with `content: []`, a thinking-only content list, or only whitespace text may be handled by the client as a silent end of turn or as an error; this has not been verified against the client. Keep the decision (faithful translation) and see Open items.

---

## 9. stop_reason

`finish_reason` is normalised by trimming ASCII whitespace and lowercasing. Length reasons: `length`, `max_tokens`, `max_output_tokens`, `max_completion_tokens`, `model_context_window_exceeded`.

Decision procedure (first match wins):
1. At least one `tool_use` block is present in the final content -> `tool_use` (regardless of `finish_reason`).
2. `finish_reason` is a length reason -> `max_tokens`.
3. Anything else -> `end_turn`.

| Upstream `finish_reason` | tool_use block emitted | Result |
|---|---|---|
| `stop` | yes | `tool_use` |
| `stop` | no | `end_turn` |
| `tool_calls` / `function_call` | yes | `tool_use` |
| `tool_calls` / `function_call` | no (all calls dropped) | `end_turn` |
| `length` and the other length reasons | no | `max_tokens` |
| `length` and the other length reasons | yes (a call survived, others may have been dropped) | `tool_use` |
| `content_filter` | either | per rows above; never a refusal stop reason |
| absent, `null`, empty, unknown (e.g. `sensitive`, `eos`, `end_turn`, `stop_sequence`, `error`) | no | `end_turn` |

`stop_sequence` is always `null`; the bridge MUST NOT produce `stop_reason: "stop_sequence"`, `"refusal"` or `"pause_turn"`.

Risk: a `length` reply that still contains complete earlier tool calls returns `tool_use` (the surviving calls run, the truncated call is gone); the alternative reading (`max_tokens`) was rejected because the client executes tool_use blocks either way and `tool_use` avoids a spurious output-limit recovery.
Risk: `content_filter` with no refusal text yields an empty message with `end_turn`; the user gets no explanation.

---

## 10. Usage

### 10.1 Field reading

A value is a "count" only if it is a JSON number that is finite and >= 0; fractional values are floored. Negative numbers, strings (including numeric strings), booleans, `null` and non-finite values are treated as absent. Where several candidate fields exist, the first count in the listed order wins (standard fields before vendor fields).

| Quantity | Candidate fields, in priority order |
|---|---|
| prompt | `usage.prompt_tokens`; `usage.input_tokens`; `usage.prompt_cache_hit_tokens` + `usage.prompt_cache_miss_tokens` (only if both are counts) |
| completion | `usage.completion_tokens`; `usage.output_tokens`; if `usage.total_tokens` and prompt are counts: `total_tokens - prompt` (floor 0) |
| cache read | `usage.prompt_tokens_details.cached_tokens`; `usage.input_tokens_details.cached_tokens`; `usage.cached_tokens`; `usage.prompt_cache_hit_tokens`; `usage.cache_read_input_tokens`; `usage.cache_read_tokens` |
| cache creation | `usage.prompt_tokens_details.cache_write_tokens`; `usage.prompt_tokens_details.cache_creation_tokens`; `usage.input_tokens_details.cache_write_tokens`; `usage.input_tokens_details.cache_creation_tokens`; `usage.cache_creation_input_tokens`; `usage.cache_creation_tokens`; `usage.cache_write_tokens` |

Other members (reasoning-token breakdowns, audio tokens, `total_tokens` when prompt/completion are present, durations, cost fields) are ignored. `completion` includes reasoning tokens when the upstream counts them so; no adjustment is made.

### 10.2 Computation

- A reported prompt of 0 is treated as absent. A reported completion of 0 is treated as absent when the message has any output (any block).
- prompt = reported prompt, else the request estimate from the context.
- completion = reported completion, else the shared estimator (04) applied to the concatenation of the emitted thinking text, text, and for each emitted tool_use its (original) name followed by its compact input JSON. An empty output estimates to 0.
- cache_read = reported cache read, else 0. cache_creation = reported cache creation, else 0.
- `input_tokens = max(0, prompt - cache_read - cache_creation)`.

Emitted `usage` object, member order: `input_tokens`, `cache_creation_input_tokens`, `cache_read_input_tokens`, `output_tokens`; all JSON integers >= 0. When the usage is wholly or partly estimated the cache members are 0 unless reported. The bridge MUST NOT emit an empty or partial usage object, `null` members, or members beyond these four.

Risk: subtracting a reported cache-creation count from the prompt (in addition to cache read) extends the minimal "prompt minus cache read" rule so that `input_tokens` keeps the Messages meaning (excludes both cache counters). If an upstream reports creation tokens that are not inside its prompt count, `input_tokens` is understated; see Open items.

---

## 11. Errors

### 11.1 Envelope

Every failure MUST reach the client as `{"type":"error","error":{"type":<T>,"message":<M>}}` (a top-level `request_id` string MAY be added) with the HTTP status given below. No other body shape is permitted, for any status. Successful and failed responses are both produced by the bridge; errors MUST NOT be wrapped in any aggregate or diagnostic structure.

### 11.2 Type and status by upstream status

The upstream HTTP status is kept unless a row says otherwise.

| Upstream status | `error.type` |
|---|---|
| 400 | `invalid_request_error` |
| 401 | `authentication_error` |
| 403 | `permission_error` |
| 404 | `not_found_error` |
| 408, 504 | `timeout_error` |
| 413 | `request_too_large` |
| 429 | `rate_limit_error` |
| 500 | `api_error` |
| 502 | `api_error` |
| 503, 529 | `overloaded_error` |
| any other 4xx (402, 405, 409, 422, ...) | `invalid_request_error` |
| any other 5xx | `api_error` |
| non-2xx below 400 (unexpected redirects) | status 502, `api_error` |

Context-length overflow overrides the table: if the upstream error carries code `context_length_exceeded` (in `error.code` or `error.type`) or its message matches an overflow pattern (for example "maximum context length", "prompt is too long", "exceeds the context window"; the authoritative pattern list and the exact output wording are defined in 04), the bridge MUST emit HTTP 400, type `invalid_request_error`, with the message required by 04 (it starts with `prompt is too long`). This applies regardless of the upstream status and to in-band errors.

Risk: keeping 401/403 from an upstream key problem makes the client believe its own credentials are invalid (it may re-run its key helper or prompt for login). The decision keeps the upstream status and the upstream's own message; the user-visible text is therefore the only hint that the fault is the upstream credential.
Risk: a 503 mapped to `overloaded_error` keeps status 503; the client's overload-specific handling is keyed on 529, so only its generic 5xx retry applies.

### 11.3 Message

- The message is the upstream's own text, extracted from the first available of: `error.message` (string); `error` (string); `message` (string); `detail` (string, or an array whose string elements and `msg` members are joined with `"; "`); `errors[0].message`; and, if the body is not JSON, the body text itself.
- Truncate to at most 2,000 characters (UTF-16 code units, never splitting a surrogate pair); no marker is appended.
- If the body is empty, or is JSON without any of the members above, or yields no text: `Upstream returned HTTP <status> with no error message.`
- If the body is an HTML document (first non-whitespace character `<`): `Upstream returned an HTML error page (HTTP <status>).` The HTML MUST NOT be echoed.
- Credentials, upstream URLs and internal identifiers MUST NOT be added to the message.
- The extracted message is then post-processed as specified in `04-harness-and-errors.md` section 7 (upstream-credential prefix for 401/403, redaction of the configured key, context-overflow rewording); that section is authoritative where it differs from this one.

### 11.4 Bridge-originated failures

| Condition | Status | `error.type` | Message (SHOULD) |
|---|---|---|---|
| Upstream 2xx body is empty, not JSON, not an object, or lacks a `choices` array (and is not an in-band error) | 502 | `api_error` | `Upstream returned a response that is not a Chat Completions object.` |
| Connection refused / DNS / TLS / reset before a response | 502 | `api_error` | `Failed to reach the upstream provider.` |
| No response (or body) within the deadline | 504 | `timeout_error` | `Upstream request timed out.` |
| Unrepairable tool arguments (7.4); nameless sole tool call (7.2) | 502 | `api_error` | as specified there |
| Client disconnected | no response is written | - | - |

Deadlines: a non-streaming reply from a reasoning model may take minutes before the first byte; the bridge/router MUST NOT impose a total deadline shorter than the client's own request timeout (SHOULD be at least 600 s). See Open items.

### 11.5 In-band errors on a 2xx reply

If the body has an `error` object (or non-empty string) and `choices` is not an array, it is an error, never a 200 message. Status = numeric `error.code` or `error.status` when that is an integer 400-599, else 502. Type from 11.2 (502 -> `api_error`). Message per 11.3. If the selected choice has an object member `error` and the choice has no text, reasoning or tool calls, the same treatment applies.

### 11.6 Headers on errors

`retry-after` and `retry-after-ms` from the upstream response MUST be forwarded when present and valid; invalid values are dropped as specified in `04-harness-and-errors.md` section 7.4. No other upstream header is forwarded. Bridge-originated failures MAY carry `retry-after` for 429/503/529 equivalents only if the bridge knows a value.

The bridge needs the upstream status and the two retry headers in addition to the body; this is a requirement on the router seam (see Open items).

---

## 12. Response headers (success)

| Header | Requirement |
|---|---|
| Status | 200 |
| `content-type` | `application/json` |
| `content-length` / `content-encoding` / `transfer-encoding` | MUST NOT be copied from the upstream; they describe the upstream body, not the rewritten one. The transport recomputes them. |
| Other upstream headers (rate-limit, request ids, processing times, vendor metadata) | MUST NOT be forwarded. |
| `request-id` | MAY be set to `req_` + 24 characters `[0-9A-Za-z]`; if an error body carries a `request_id` member it MUST equal the `request-id` header. |
| Router diagnostic/accounting headers | outside this specification |

---

## 13. Upstream that answers with an event stream

The bridge sends no `stream` flag (or `stream:false`) for a non-streaming client, so a conforming upstream answers with one JSON document. If the reply has an event-stream content type, or the body begins with `data:`, `event:` or `:`, the bridge MAY aggregate it into one message using the accumulation rules of 03 (text and reasoning appended in order, tool-call fragments joined per index, last `finish_reason`, last usage) and then apply this file; if aggregation is not implemented it MUST return HTTP 502 `api_error` (11.4). A stream that ended without a `finish_reason` and without `[DONE]` MUST be treated as an upstream failure (502 `api_error`), not as a successful message.

---

## 14. Implementation requirements (.NET)

- MUST use `JsonNode`/`JsonDocument`/`Utf8JsonWriter` only; no reflection-based serialization (NativeAOT).
- MUST distinguish JSON `null`, missing and wrong-typed members everywhere; numeric strings are not numbers.
- MUST preserve numeric text of tool arguments (write the raw token, never a converted `double`/`decimal`); duplicate property names MUST NOT throw; strict parser options (no comments, no trailing commas, depth 64).
- MUST NOT trim, normalise or re-encode any text, reasoning or argument string except where this file says so; comparisons for finish reasons and field names are ordinal.
- Output MUST be compact JSON; escaping of non-ASCII characters is optional but text content MUST round-trip exactly.
- Ids and generated suffixes MUST come from `RandomNumberGenerator`.
- Truncation of error text MUST operate on UTF-16 code units without splitting surrogate pairs.
- The conversion MUST hold no static mutable state; the name map and estimate arrive per call.

---

## 15. Edge-case matrix

Defaults for every row unless stated: inbound model `claude-sonnet-4-5`; request estimate R = 1500; empty name map; upstream status 200; choice `index` 0; `finish_reason` `stop`; usage `{"prompt_tokens":100,"completion_tokens":10}`, which maps to U0 = `{"input_tokens":100,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":10}`. E(x) = the shared estimator applied to x. `msg_*` / `toolu_*` = 24 random `[0-9A-Za-z]` characters. "Message" = HTTP 200 Messages object with `model` `claude-sonnet-4-5`, `stop_sequence` null and a `msg_*` id, whose other members are given.

### 15.1 Envelope, choices, text

| # | Case | Upstream | Target result |
|---|---|---|---|
| 1 | Plain text | `content:"Hello"` | Message: content `[{"type":"text","text":"Hello"}]`, `end_turn`, U0 |
| 2 | Upstream id present | `id:"chatcmpl-1"` | `id` is a fresh `msg_*`; upstream id absent from output |
| 3 | Upstream id missing | no `id` | fresh `msg_*` |
| 4 | Upstream model differs/absent | `model:"gpt-4o-2024-08-06"` / absent | `model` is `claude-sonnet-4-5` |
| 5 | Inbound model with suffix | inbound `claude-opus-4-5[1m]` | `model` is `claude-opus-4-5[1m]` byte for byte |
| 6 | Two responses | same upstream body twice | two different `msg_*` ids |
| 7 | Leading/trailing whitespace | `content:"  \n  def f():\n    pass\n"` | text exactly `"  \n  def f():\n    pass\n"` |
| 8 | Whitespace-only content, no calls | `content:" \n "` | content `[{"type":"text","text":" \n "}]`, `end_turn` |
| 9 | Empty string content | `content:""` | content `[]`, `end_turn`, U0 |
| 10 | Null content, no calls | `content:null` | content `[]`, `end_turn`, U0 |
| 11 | Array parts concatenate | `[{"type":"text","text":"a"},{"type":"text","text":"b"}]` | text `"ab"` |
| 12 | Array with string elements | `["x","y"]` | text `"xy"` |
| 13 | Array with non-text parts | `[{"type":"image_url","image_url":{}},{"type":"text","text":"z"}]` | text `"z"` |
| 14 | Content is an object | `content:{"text":"q"}` | no text; content `[]` |
| 15 | Refusal only | `content:null, refusal:"I can't help with that."` | text block `"I can't help with that."`, `end_turn` |
| 16 | Refusal with `content_filter` | as 15, `finish_reason:"content_filter"` | same as 15 (`end_turn`) |
| 17 | Content and refusal | `content:"A", refusal:"B"` | text `"A\nB"` |
| 18 | Refusal as array part | `[{"type":"refusal","refusal":"No."}]` | text `"No."` |
| 19 | Think markup, default | `content:"<think>x</think>y"` | text `"<think>x</think>y"` |
| 20 | `choices` empty | `choices:[]`, usage U0 inputs | content `[]`, `end_turn`, U0 |
| 21 | `choices` missing | `{"id":"x"}` | HTTP 502 `api_error` envelope |
| 22 | `choices` not an array | `choices:{}` | HTTP 502 `api_error` |
| 23 | Selection by index | `choices:[{index:1,"message":{"content":"B"}},{index:0,"message":{"content":"A"}}]` | text `"A"` |
| 24 | No index | `choices:[{"message":{"content":"A"}},{"message":{"content":"B"}}]` | text `"A"` |
| 25 | Selected choice not an object | `choices:[null]` | content `[]`, `end_turn` |
| 26 | `delta` instead of `message` | `choices:[{"delta":{"content":"d"}}]` | text `"d"` (MAY) or content `[]`; both conform |

### 15.2 Reasoning

| # | Case | Upstream message | Target result |
|---|---|---|---|
| 27 | `reasoning_content` | `reasoning_content:"think", content:"ans"` | content `[thinking "think" (signature ""), text "ans"]` |
| 28 | Field precedence | `reasoning_content:"a", reasoning:"b", thinking:"c"` | thinking `"a"` |
| 29 | `reasoning` only | `reasoning:"r"` | thinking `"r"` |
| 30 | Empty first field | `reasoning_content:"", reasoning:"r"` | thinking `"r"` |
| 31 | Whitespace first field | `reasoning_content:"  ", reasoning:"r"` | thinking `"r"` |
| 32 | Whitespace preserved | `reasoning_content:" a\n b "` | thinking `" a\n b "` |
| 33 | Details text entry | `reasoning_details:[{"type":"reasoning.text","text":"t"}]` | thinking `"t"` |
| 34 | Details + identical plain | details text `"t"`, `reasoning_content:"t"` | one thinking `"t"` |
| 35 | Details + identical plain, padded | details `"t"`, plain `" t\n"` | one thinking `"t"` |
| 36 | Details + distinct plain | details `"d"`, plain `"p"` | thinking `"p\nd"` |
| 37 | Summary entry | `[{"type":"reasoning.summary","summary":"s"}]` | thinking `"s"` |
| 38 | String entries | `["a","  ","b"]` | thinking `"a\nb"` |
| 39 | Two text entries | `[{"text":"x"},{"text":"y"}]` | thinking `"x\ny"` |
| 40 | Encrypted only | `[{"type":"reasoning.encrypted","data":"abc"}]`, `content:"ok"` | content `[text "ok"]` (no thinking block) |
| 41 | Encrypted + text entry | text `"t"` and encrypted entry | thinking `"t"` only |
| 42 | Upstream signature | `[{"type":"reasoning.text","text":"t","signature":"SIG"}]` | thinking `"t"`, `"signature":""` |
| 43 | Redacted type | `[{"type":"redacted_thinking","data":"z"}]` | no thinking block |
| 44 | Reasoning only, no text | `reasoning_content:"t", content:null` | content `[thinking "t"]`, `end_turn`, U0 |
| 45 | Reasoning without request thinking | any request | thinking emitted |
| 46 | Order | reasoning + text + call | thinking, text, tool_use |

### 15.3 Tool calls

| # | Case | Upstream | Target result |
|---|---|---|---|
| 47 | One call | `tool_calls:[{id:"call_1",function:{name:"Read",arguments:"{\"p\":\"a\"}"}}]`, `finish_reason:"tool_calls"` | content `[tool_use{id:"call_1",name:"Read",input:{"p":"a"}}]`, `tool_use` |
| 48 | Text + two parallel calls | `content:"Go", 2 calls` | `[text "Go", tool_use, tool_use]` in call order, `tool_use` |
| 49 | Missing id | call without `id` | id `toolu_*` |
| 50 | Empty id | `id:""` | id `toolu_*` |
| 51 | Illegal characters | `id:"functions.Bash:0"` | id `toolu_*` |
| 52 | 64-char valid id | 64 chars of `[a-z0-9_-]` | kept |
| 53 | 65-char id | 65 chars | id `toolu_*` |
| 54 | Duplicate ids | two calls `id:"call_0"` | first kept `call_0`; second `toolu_*` |
| 55 | Synthesized id collision | synthesized value equals another id in the message | regenerated; all ids distinct |
| 56 | Non-string id | `id:7` | id `toolu_*` |
| 57 | Name map reverse | map `{"mcp__my_srv__t":"mcp__my.srv__t"}`, name `mcp__my_srv__t` | name `mcp__my.srv__t` |
| 58 | Name not in map | name `Weird` | `Weird` unchanged |
| 59 | Name map case mismatch | map key `Read`, upstream `read` | `read` unchanged |
| 60 | Name from top level | `{id:"a",name:"Read",arguments:"{}"}` | name `Read` |
| 61 | Nameless call among others | one call without name, one valid | nameless dropped; valid kept |
| 62 | Nameless sole call | only call has no name, no text | HTTP 502 `api_error` `Upstream returned a tool call without a name.` |
| 63 | Nameless call with text | no-name call + `content:"hi"` | content `[text "hi"]`, `end_turn` |
| 64 | Args empty string | `arguments:""` | input `{}` |
| 65 | Args whitespace | `arguments:" \n"` | input `{}` |
| 66 | Args absent | no arguments member | input `{}` |
| 67 | Args `null` | `arguments:null` | input `{}` |
| 68 | Args object (non-string) | `arguments:{"a":1}` | input `{"a":1}` |
| 69 | Args via `input` member | `{id,name,input:{"p":1}}` | input `{"p":1}` |
| 70 | Numbers keep text | `arguments:"{\"a\":1.0,\"b\":1e2,\"c\":12345678901234567890}"` | input `{"a":1.0,"b":1e2,"c":12345678901234567890}` |
| 71 | Compact output | `arguments:"{ \"a\" : [ 1 , 2 ] }"` | input `{"a":[1,2]}` |
| 72 | Duplicate keys | `arguments:"{\"a\":1,\"a\":2}"` | input `{"a":2}` |
| 73 | Repair: open string | `arguments:"{\"a\":\"x"`, finish `tool_calls` | input `{"a":"x"}` |
| 74 | Repair: open array | `arguments:"{\"a\":[1,2"` | input `{"a":[1,2]}` |
| 75 | Repair: nested | `arguments:"{\"a\":{\"b\":\"c\""` | input `{"a":{"b":"c"}}` |
| 76 | Repair: partial escape | `arguments:"{\"a\":\"x\\"` | input `{"a":"x"}` |
| 77 | Repair: dangling comma | `arguments:"{\"a\":1,"` | input `{"a":1}` |
| 78 | Unrepairable: missing value | `arguments:"{\"a\":"`, finish `tool_calls` | HTTP 502 `api_error` `Upstream returned invalid arguments for tool call "<name>".` |
| 79 | Unrepairable: dangling key | `arguments:"{\"a\":1,\"b\""` | HTTP 502 `api_error` |
| 80 | Non-object JSON | `arguments:"[1,2]"` / `"\"x\""` / `"5"` / `"null"` | HTTP 502 `api_error` (each) |
| 81 | Double-encoded string | `arguments:"\"{\\\"a\\\":1}\""` | HTTP 502 `api_error` |
| 82 | Trailing garbage | `arguments:"{\"a\":1} extra"` | HTTP 502 `api_error` |
| 83 | Length: unrepairable call dropped | finish `length`, only call has `arguments:"{\"a\":"` | content `[]`, `max_tokens` |
| 84 | Length: earlier call survives | finish `length`, call A valid, call B `{"b":` | content `[tool_use A]`, `tool_use` |
| 85 | Length: text kept | `content:"Let me", call truncated`, finish `length` | content `[text "Let me"]`, `max_tokens` |
| 86 | Length: truncated but repairable | finish `length`, `arguments:"{\"a\":\"x"` | repaired call kept: `[tool_use input {"a":"x"}]`, `tool_use` |
| 87 | `tool_calls` is an object | `tool_calls:{...}` | ignored; no tool_use |
| 88 | Legacy function_call | `content:null, function_call:{name:"f",arguments:"{}"}`, finish `function_call` | `[tool_use{id:toolu_*,name:"f",input:{}}]`, `tool_use` |
| 89 | Both shapes | `tool_calls:[A], function_call:B` | only A |
| 90 | Legacy call, bad args | `function_call:{name:"f",arguments:"{"}`, finish `stop` | HTTP 502 `api_error` |
| 91 | `index`/`type` ignored | calls with odd `index`, `type:"custom"` but valid name/args | processed in array order |
| 92 | Non-object element | `tool_calls:[7,{valid}]` | 7 skipped; valid kept |
| 93 | Name restored in reasoning-free call | map hit, text empty | name original, content `[tool_use]` |

### 15.4 stop_reason

| # | Case | Upstream | Target result |
|---|---|---|---|
| 94 | stop, no calls | `stop` | `end_turn` |
| 95 | stop with calls | `stop` + 1 valid call | `tool_use` |
| 96 | tool_calls, all dropped | finish `tool_calls`, text `"ok"`, only call nameless | content `[text "ok"]`, `end_turn` |
| 97 | length | `length`, text `"abc"` | `max_tokens` |
| 98 | length, empty | `length`, `content:""` | content `[]`, `max_tokens` |
| 99 | Length variants | `max_tokens`, `MAX_TOKENS`, `max_output_tokens`, `LENGTH`, `model_context_window_exceeded` | `max_tokens` (with no surviving call) |
| 100 | content_filter | `content_filter`, `content:""` | content `[]`, `end_turn` |
| 101 | Missing finish | no `finish_reason`, text | `end_turn` |
| 102 | Null/empty finish | `null` / `""` | `end_turn` |
| 103 | Unknown vendor value | `sensitive`, `eos`, `network_error`, `FOO_MAX_BAR` | `end_turn` (no substring matching) |

### 15.5 Usage

| # | Case | Upstream usage | Target usage |
|---|---|---|---|
| 104 | Standard | prompt 1200, completion 6, `prompt_tokens_details.cached_tokens` 1024 | `{"input_tokens":176,"cache_creation_input_tokens":0,"cache_read_input_tokens":1024,"output_tokens":6}` |
| 105 | Cached exceeds prompt | prompt 10, cached 60 | input 0, cache_read 60 |
| 106 | Cached zero | cached 0 | input = prompt, cache_read 0 |
| 107 | Vendor hit/miss | prompt 100, `prompt_cache_hit_tokens` 60, `prompt_cache_miss_tokens` 40 | input 40, cache_read 60 |
| 108 | Hit/miss without prompt | no prompt, hit 60, miss 40 | prompt 100 -> input 40, cache_read 60 |
| 109 | Top-level cached | prompt 100, `cached_tokens` 30 | input 70, cache_read 30 |
| 110 | Anthropic-style names | prompt 100, `cache_read_input_tokens` 30, `cache_creation_input_tokens` 20 | input 50, creation 20, read 30 |
| 111 | Details creation names | `prompt_tokens_details.cache_write_tokens` 20 and `cache_creation_tokens` 99 | creation 20 (first wins) |
| 112 | Standard beats vendor | `prompt_tokens_details.cached_tokens` 70, `prompt_cache_hit_tokens` 80 | cache_read 70 |
| 113 | Invalid cached values | cached `-5` / `"30"` / `null` / `1e999` | treated absent; cache_read 0 |
| 114 | Fractional | prompt 100.9, completion 10.2 | prompt 100, completion 10 |
| 115 | Responses-style names | `input_tokens` 5, `output_tokens` 6 | input 5, output 6 |
| 116 | Both naming sets | `prompt_tokens` 9, `input_tokens` 5 | prompt 9 |
| 117 | Total only | prompt 100, `total_tokens` 130, no completion | output 30 |
| 118 | Usage absent | none | input R=1500, output E(emitted output), cache 0/0 |
| 119 | Usage absent, empty output | none, `content:""` | input 1500, output 0, cache 0/0 |
| 120 | Prompt only | prompt 7, no completion, text `"Hello there!"` | input 7, output E("Hello there!") |
| 121 | Completion only | completion 9, no prompt | input 1500, output 9 |
| 122 | All zero, output present | prompt 0, completion 0, text present | input 1500, output E(output) |
| 123 | Prompt zero, completion real | prompt 0, completion 9 | input 1500, output 9 |
| 124 | Completion zero, empty output | prompt 100, completion 0, `content:""` | input 100, output 0 |
| 125 | Numeric strings | prompt `"100"`, completion `"10"` | treated absent; estimates used |
| 126 | Usage in choice | top-level absent, `choices[0].usage` set | choice usage used |
| 127 | Reasoning tokens ignored | `completion_tokens_details.reasoning_tokens` 24 | no effect; output = completion |
| 128 | Estimate covers tool call | no usage, one call | output E(name + compact input JSON) |
| 129 | Estimate covers thinking | no usage, reasoning + text | output E(thinking + text) |

### 15.6 Errors and headers

| # | Case | Upstream | Target result |
|---|---|---|---|
| 130 | 429 JSON with Retry-After | 429, `{"error":{"message":"Rate limit"}}`, `Retry-After: 7` | 429 `rate_limit_error` "Rate limit", header `retry-after: 7` |
| 131 | `retry-after-ms` | 429, `retry-after-ms: 1500` | forwarded unchanged |
| 132 | 401 | 401 | 401 `authentication_error`, upstream message kept |
| 133 | 403 | 403 | 403 `permission_error` |
| 134 | 404 | 404 | 404 `not_found_error` |
| 135 | 400 | 400 `{"error":{"message":"bad"}}` | 400 `invalid_request_error` "bad" |
| 136 | 413 | 413 | 413 `request_too_large` |
| 137 | 422 | 422 | 422 `invalid_request_error` |
| 138 | 402 | 402 | 402 `invalid_request_error` |
| 139 | 408 | 408 | 408 `timeout_error` |
| 140 | 504 | 504 | 504 `timeout_error` |
| 141 | 500 | 500 | 500 `api_error` |
| 142 | 502 | 502 | 502 `api_error` |
| 143 | 503 | 503 | 503 `overloaded_error` |
| 144 | 529 | 529 | 529 `overloaded_error` |
| 145 | 501 | 501 | 501 `api_error` |
| 146 | HTML body | 502, `<html>...` | 502 `api_error`, `Upstream returned an HTML error page (HTTP 502).` |
| 147 | Empty body | 500, empty | 500 `api_error`, `Upstream returned HTTP 500 with no error message.` |
| 148 | Plain text body | 400, `bad thing` | 400 `invalid_request_error` "bad thing" |
| 149 | Long message | 400 with 5,000-character message | message exactly the first 2,000 characters; no surrogate split |
| 150 | `detail` string | 400 `{"detail":"nope"}` | message `nope` |
| 151 | `detail` array | 422 `{"detail":[{"msg":"a"},{"msg":"b"}]}` | message `a; b` |
| 152 | `error` as string | 500 `{"error":"boom"}` | message `boom` |
| 153 | Context overflow by code | 400 `{"error":{"code":"context_length_exceeded","message":"maximum context length is 128000 tokens ..."}}` | 400 `invalid_request_error`, message per 04 starting `prompt is too long` |
| 154 | Overflow with other status | 413 or 422 with message "exceeds the context window" | 400 `invalid_request_error`, message per 04 |
| 155 | In-band error, numeric code | 200 `{"error":{"code":429,"message":"slow down"}}` | 429 `rate_limit_error` "slow down" |
| 156 | In-band error, no code | 200 `{"error":{"message":"x"}}` | 502 `api_error` "x" |
| 157 | Choice-level error | `choices:[{"error":{"message":"m"},"finish_reason":"error"}]`, nothing else | 502 `api_error` "m" |
| 158 | 200 empty body | 200, empty | 502 `api_error`, not-a-Chat-object message |
| 159 | 200 HTML | 200, `<html>` | 502 `api_error` |
| 160 | 200 JSON scalar/array | `"ok"` / `[]` | 502 `api_error` |
| 161 | Unexpected 3xx | 304, empty | 502 `api_error` |
| 162 | Timeout | no reply within deadline | 504 `timeout_error`, `Upstream request timed out.` |
| 163 | Connection refused | transport failure | 502 `api_error`, `Failed to reach the upstream provider.` |
| 164 | Client disconnect | client closes first | nothing written |
| 165 | Success headers | any 200 | `content-type: application/json`; no upstream `content-length`, `content-encoding`, rate-limit or request-id headers |
| 166 | Error body shape | any error | exactly `{"type":"error","error":{"type":T,"message":M}}` (optional `request_id`); no `attempts`/`target_*` members |
| 167 | Event-stream reply | 200 `text/event-stream` to a non-stream request | aggregated message (MAY) or 502 `api_error` |
| 168 | Event-stream truncated | stream ends, no `finish_reason`, no `[DONE]` | 502 `api_error` |

---

## 16. Open items (need live-upstream measurement)

1. Client behaviour with `content: []`, thinking-only content, whitespace-only text and `content_filter` with no text (sections 8.2, 9): confirm the turn ends sanely and whether a user-visible hint is needed.
2. Whether upstreams that report cache-creation tokens include them inside `prompt_tokens` (section 10.2); and whether any upstream reports an exclusive `prompt_tokens` (cached > prompt), which would need a per-upstream option.
3. How often upstreams return all-zero or missing usage on non-streaming replies, to validate the fallback estimator error against real counts (04).
4. The real `finish_reason` vocabulary of each target upstream (length-like aliases other than the five listed; abnormal values such as `error`).
5. Segmentation of `reasoning_details` (are entries separate thinking segments or fragments?), to confirm joining entries with a single line feed is the right display.
6. Whether any target upstream double-encodes tool arguments (a JSON string containing JSON) often enough to justify a one-level unwrap before validation.
7. Frequency and shape of in-band errors on 2xx replies, including choice-level `error` objects (11.5).
8. Whether the router seam gives the non-streaming conversion the upstream status and `retry-after*` headers for non-2xx replies (11.6); if it does not, the error mapping cannot meet its contract.
9. A safe default total deadline for non-streaming replies from reasoning models, aligned with the client's request timeout (11.4).
10. Which upstreams emit think-markup inside `content` and therefore need the optional extraction (5.3).
11. Whether any upstream answers a non-streaming request with an event stream (13) and whether aggregation is worth implementing.
12. Name drift on the upstream side (case changes, added namespace prefixes) versus the exact-match reverse map (7.2).
13. Repair statistics: how often the bounded repair (7.4) fires on non-length finishes, and whether any repaired call changes tool semantics (for example a truncated trailing number).
