# 04 - Harness interface, token estimator, outbound headers and error contract (normative)

Status: FINAL target specification. Language: MUST / SHOULD / MAY as in RFC 2119.
Siblings: `01-request-mapping.md` (Messages request -> Chat request), `02-response-mapping.md` (Chat reply -> Messages reply, non-stream), `03-stream-state-machine.md` (Chat SSE -> Messages SSE).
Binding cross-cutting decisions (D1-D21) are referenced as "Dn". References to sibling sections name the topic so they survive renumbering.

Vocabulary: "bridge" = the component translating a Claude Code Messages request into a Chat Completions request and the reply back. "Client" = Claude Code (the CLI). "Upstream" = the Chat-only provider. "Router" = the existing host that authenticates the client, selects the route, renames the model and forwards the request.

This file is authoritative for: the endpoint surface, the shared token estimator, the outbound header allowlist, client-attribution handling, the context-overflow re-emission wording, upstream-credential-failure wording, and client-facing error semantics. Field-by-field request mapping stays in 01; reply mapping in 02 / 03.

---------------------------------------------------------------------------------------------------

## 1. Endpoint surface

### 1.1 Table (bridged route)

| Endpoint | Method | Behavior |
|---|---|---|
| `/v1/messages` (a query string such as `?beta=true` is ignored and never forwarded) | POST | Bridged: 01 -> one upstream attempt (D21) -> 02 (`stream` false or absent) or 03 (`stream` true). |
| `/v1/messages/count_tokens` (query ignored) | POST | Served locally (1.3). No upstream request; no provider credential used. |
| `/v1/messages`, `/v1/messages/count_tokens` | any other method | HTTP 405, envelope type `invalid_request_error`, message `Method Not Allowed`, header `allow: POST`. |
| `/v1/models`, `/v1/models/{id}` | GET | Not handled by the bridge; answered by the router's existing handler. |
| Any other path that reaches the bridge's error path: `/v1/messages/batches*`, `/v1/files*`, `/v1/skills*`, `/v1/complete`, `/v1/organizations/*`, `/v1/oauth/*`, `/api/*` (bootstrap, event logging, OAuth, hello) | any | HTTP 404, type `not_found_error`, message `Not found: <METHOD> <path>` (path without query). The bridge implements none of these APIs (D16). |

The router decides whether a request is bridged (route match). The bridge never inspects credentials; client authentication has already happened. Requests whose model is not on a bridged route are outside this file.

### 1.2 Rules common to both POST endpoints

- The body is a UTF-8 JSON text already decoded by the router. A body that is not a JSON object (including invalid JSON) -> HTTP 400, `invalid_request_error`, message `Request body must be a JSON object.` (same text as 01).
- Request headers never change behavior except as stated in section 2. `anthropic-version` may be absent or any value; `anthropic-beta` is ignored.
- The bridge keeps no state between requests; every request is processed from its own body only.

### 1.3 `POST /v1/messages/count_tokens`

Request: a JSON object shaped like a Messages request. Only `messages`, `system` and `tools` are read; `model`, `tool_choice`, `thinking`, `metadata` and everything else are ignored.

Validation (the only rejections):
- `messages` missing or null -> 400 `invalid_request_error`, `messages: Field required`.
- `messages` not a list -> 400 `invalid_request_error`, `messages: Input should be a valid list`.

An empty list is accepted. `system` / `tools` of an unusable kind count as absent. Unsupported content (documents, server-tool blocks, typed server tool definitions, thinking blocks, unknown block types) MUST NOT cause a rejection here; it is counted as defined in 1.4.2.

Response: HTTP 200, `content-type: application/json`, body exactly `{"input_tokens":N}` (compact, no other members), N from 1.4.2. The same body always yields the same N. The call performs no I/O and MUST NOT depend on upstream availability.

### 1.4 Shared token estimator

One estimator serves: `count_tokens` (1.3); the request estimate placed in the request-derived context (01, "Request-derived context"), which supplies `message_start.usage.input_tokens` and every missing-input fallback (02 "Usage", 03 "Usage and stop_reason"); and the output-side fallback (02, 03). It is a pure, integer-only heuristic.

#### 1.4.1 Text units

A token is 4 units. For a string s, `U(s)` = the sum of `weight(c)` over the Unicode scalar values c of s (an unpaired surrogate or invalid byte counts as one scalar of weight 4). Strings are never trimmed or normalised.

| Scalar values | weight (units) |
|---|---|
| ASCII letters, ASCII digits, space, tab, LF, CR | 1 |
| every other ASCII scalar (punctuation, symbols, other controls) | 2 |
| U+0080 - U+07FF | 2 |
| U+2E80 - U+9FFF, U+AC00 - U+D7AF, U+F900 - U+FAFF, U+FF00 - U+FFEF, U+20000 - U+2FFFF | 6 |
| U+10000 - U+1FFFF (emoji and other supplementary scalars) | 8 |
| everything else (U+0800 - U+2E7F, U+A000 - U+ABFF, other BMP, unpaired surrogates) | 4 |

`EST(s) = ceil(U(s) / 4)`; `EST("") = 0`. Callers apply their own minimums (02 and 03 state them).

`compact(v)` = the JSON text of v without insignificant whitespace, members in received order, strings escaped only where JSON requires (quote, backslash, control characters below U+0020), non-ASCII emitted literally, numbers in their received text.

#### 1.4.2 Request estimate

`RequestEstimate(body) = ceil(Ureq / 4)` where `Ureq` is 12 plus the parts below, evaluated on the inbound Messages body as received:

| Part | Units |
|---|---|
| `system` string | `U(string)` |
| `system` list, after the attribution block is dropped (3.1): the `text` of every remaining element that is a text block with a string `text`, joined with `\n` | `U(joined)` (0 when nothing remains) |
| each element of `messages` that is an object | 16, plus its blocks below (a string `content` counts as one text block) |
| text block (`type` `text`, string `text`) | `U(text)` |
| `image` block (any source, any size) | 6000 |
| `tool_use` block | 24 + `U(name)` + `U(compact(input))` (`input` absent or null counts as `{}`) |
| `tool_result` block | 24 + content: string -> `U(string)`; list -> `U(` the nested text blocks' texts joined with `\n` `)`, plus 6000 per nested `image`, plus `U(compact(block))` per nested `tool_reference`, plus the placeholder rule of the "any other block" row per other nested block |
| `thinking`, `redacted_thinking` block (dropped by 01) | 0 |
| any other block (document, server-tool and server-tool-result blocks, unknown types), which 01 replaces by the text `[omitted: unsupported content block '<type>']` | `U(placeholder)`, `<type>` = the block's string `type` (`unknown` when absent or not a string). A `document` block is the exception: under `documentMode: image` a base64 or URL document counts as an `image` block (6000 units) per 01 section 7.6, and only a document that does not qualify under that mode falls back to `U(placeholder)` |
| each custom tool in `tools` (no `type`, or `type` `custom`) | 48 + `U(name)` + `U(description)` + `U(compact(input_schema))` (absent or null schema counts as `{"type":"object","properties":{}}`) |
| tool-search and typed server tool definitions, any other `tools` element, every other top-level field | 0 |

Elements that are not objects contribute 0; a non-list `messages` or `tools` contributes 0.

#### 1.4.3 Output estimate

Used only when the upstream reports no usable completion count: `EST(S)`, S being the concatenation defined by 02 ("Usage": emitted thinking text, text, and for each emitted tool_use its original name followed by its compact input JSON). No constants are added.

#### 1.4.4 Properties (MUST hold)

- Pure and deterministic; no clock, randomness or I/O; single pass, O(input size).
- Additive: adding or removing one message, block or tool changes `Ureq` by exactly that item's own units, so subtracting two counts isolates the size of the difference (the client derives per-component sizes by such subtraction). Rounding happens once, at the end.
- No per-string minimum: a single-word string costs its own length, an empty string 0. A request with no messages, no system and no tools estimates to 3.
- The attribution block (3.1) never contributes, whether the estimate is computed before or after the mapping drops it.
- Bias is upward for punctuation-dense text (code, JSON: about 1.5-2 units per character) and CJK (1.5 tokens per character); plain English prose is about 1 token per 4 characters. No per-vendor calibration is applied.

Risk: the estimate can differ from a real tokenizer by tens of percent. It only gates behavior where the upstream reports no usage (message_start input count, fallback counters, `count_tokens`); the client's auto-compaction otherwise follows the final upstream-reported usage. Under-estimation delays compaction and can surface as a context-overflow error (7.5).

---------------------------------------------------------------------------------------------------

## 2. Headers

### 2.1 Inbound (client to router/bridge)

| Header | Sent by the client | Bridge behavior |
|---|---|---|
| `x-api-key`, `authorization` | gateway credential | Consumed by the router for client authentication. Never read by the bridge, never relayed. |
| `anthropic-version` | always (`2023-06-01`) | Accepted with any value or absent. Not validated, not relayed. |
| `anthropic-beta` | comma list of feature flags | Ignored entirely; no flag changes behavior (features follow D13 / D16 and the body). Not relayed. |
| `x-app`, `user-agent` (`claude-cli/<ver> (external, cli)`), `x-stainless-*`, `anthropic-dangerous-direct-browser-access`, `accept`, `accept-encoding` | SDK defaults | Ignored. Not relayed. |
| `x-claude-code-session-id` | per CLI session | Not read by the bridge. The router may use it to stamp the upstream session header (2.2); the bridge MUST NOT relay it. |
| `x-client-request-id`, `x-request-id` | possibly | Log correlation only (a log field; never relayed or echoed). |
| `content-type`, `content-length`, `content-encoding` | JSON body | Handled by the router before the bridge sees the body; the bridge does not validate them. |

### 2.2 Outbound allowlist (D20)

On a bridged route the upstream request headers MUST be built from scratch as exactly this set; nothing is copied from the client request and then filtered.

| Header | Value / source |
|---|---|
| `content-type` | `application/json` |
| `accept` | `text/event-stream` when the client asked for streaming, otherwise `application/json` |
| `authorization`, or the provider's relocated auth header name when the provider relocates the credential (for example `api-key`) | the managed credential written by the router's forwarder (not by the bridge), value format per `AuthScheme` (`Bearer` -> `Bearer <token>`, `ApiKey` -> `<token>`); never the client's credential |
| the session header the router already stamps (for example `x-opencode-session`) | passed through unchanged when the router set it; the bridge never creates, derives or modifies it |

Transport-level headers generated by the HTTP stack itself (`host`, `content-length`, `connection`, an `accept-encoding` negotiated for the stack's own decoding) are not caller headers and are permitted. No other header may be present: in particular no `cookie`, no caller-derived `user-agent`, no `anthropic-*`, `x-api-key`, `x-stainless-*`, `x-app`, `x-claude-code-*`, `x-forwarded-*`, `openai-organization`, `openai-project` or any other client-supplied header. The client's query string is not forwarded.

Risk: some providers' edge layers reject requests that carry no `User-Agent`, and some coding-plan endpoints accept only recognised agents; the allowlist offers no caller-derived value for either. A constant router-defined `user-agent` (not derived from the client) would need an explicit decision (Open items).

### 2.3 Headers returned to the client

| Case | Headers |
|---|---|
| Non-stream success | per 02 ("Response headers (success)"): `content-type: application/json`; no upstream header is relayed. |
| Stream success | per 03 (response headers). |
| Error | `content-type: application/json`; `retry-after` and `retry-after-ms` forwarded unchanged from the upstream response when present (the only two upstream headers ever relayed); `allow` for 405; `request-id` MAY be added as in 02. The bridge MUST NOT add `x-should-retry`. |

---------------------------------------------------------------------------------------------------

## 3. Claude Code request specifics

### 3.1 Client attribution block (D18)

Claude Code places, as the FIRST element of the `system` list, a text block carrying an attribution line of the form `x-anthropic-billing-header: cc_version=<version>.<fingerprint>; cc_entrypoint=<entry>; ...;` (further `key=value;` pairs may follow). Its values change from request to request. Because it is the head of the prompt, any change invalidates upstream prefix caching for the whole prompt.

Recognition (identical to 01, "System"): `system` is a list and its first element is a text block whose `text` begins with `x-anthropic-billing-header:`, compared ASCII case-insensitively from the first character, with no leading-whitespace tolerance. The whole block is dropped, whatever else its text contains. Only the first element is examined; a string-form `system` and every later element are never touched. If nothing remains no system message is produced.

The dropped block MUST NOT be forwarded, used for routing, logged above debug level, or counted by the estimator (1.4).

### 3.2 Output-limit floor (D19)

The client's startup connectivity probe is a non-streaming request with `max_tokens` 1 and a one-word user message, and several Chat upstreams reject output limits below 16. On every bridged request (stream and non-stream) an accepted `max_tokens` (an integer >= 1; see 01) below 16 MUST be raised to 16 before any configured cap is applied (01, "Sampling, limits, stop, user"). Values >= 16 are unchanged; absent or invalid values are handled by 01.

The reply is returned as the upstream produced it: the bridge MUST NOT truncate text to the client's original limit. `stop_reason` follows D1 (an upstream `length` finish gives `max_tokens`); usage is the upstream's or the estimator's.

Risk: a probe sent with `max_tokens` 1 may receive up to 16 tokens of text. The probe ignores the content; a real request with a limit below 16 (none is sent today) would receive more than it asked for.

### 3.3 Other fields (target behavior; mapping details in 01)

| Request element | What Claude Code sends | Target behavior |
|---|---|---|
| `model` | an alias string (may carry a context suffix such as `[1m]`) | The router has already renamed the body model; the bridge copies it (01) and echoes the INBOUND name (section 4). |
| `max_tokens` | 1 (probe), then 4,000 - 64,000+ | 3.2; 01 for validation and caps. |
| `system` | list; first element is the attribution block; later blocks carry `cache_control` | 3.1; 01 ("System"). `cache_control` is ignored. |
| user text | `<system-reminder>...</system-reminder>` wrappers inside ordinary text parts | Preserved byte for byte (D12). |
| `tool_result` (`is_error`, text, images, `tool_reference`) | one per tool call; text siblings in the same user message | 01 ("User messages and tool results", "Tool-result conveyed text", "Images"). |
| `image` | screenshots, pasted files | 01 ("Images"). |
| `document`, server-tool and unknown blocks (a PDF read; history carried over from an Anthropic-hosted session) | possible in history | Replaced by 01 with the text part `[omitted: unsupported content block '<type>']`; the request proceeds. Counted by the estimator (1.4.2). |
| `thinking`, `output_config.effort` | thinking enabled with a budget, adaptive or disabled; effort on supporting models | 01 ("Reasoning controls", "Vendor compat profiles"); a budget not below `max_tokens` is not an error. |
| `tools` | 20-60 custom tools including `mcp__<server>__<tool>` names, `defer_loading`, tool-search entries | 01 ("Tools"); names: section 5. A typed server tool definition (for example the web-search type used by the CLI's WebSearch sub-request) is rejected with HTTP 400 naming the tool (01); the CLI reports the failure to the model. A tool-search definition is dropped. |
| `tool_choice` | mostly `auto`; occasionally a forced tool | 01 ("tool_choice"). |
| `metadata.user_id` | a JSON string embedding device and account identifiers (newer clients) or a legacy composite string | Not sent upstream by default; sent only as the provider option dictates (01, "Sampling, limits, stop, user"). |
| `stream` | true for the main loop and compaction; false for utility calls | 01 ("Streaming"); replies per 02 / 03. |
| `context_management`, `service_tier`, `container`, `mcp_servers`, `betas`, `cache_control`, `top_k` | possible | Dropped (01, field policy). |

---------------------------------------------------------------------------------------------------

## 4. Model name (D2)

- `message.model` (02) and `message_start.message.model` (03) MUST be the inbound model name exactly as the client sent it (the value supplied to the bridge as `inboundModel`): no case, whitespace, prefix or context-suffix normalisation, never the upstream's model name, never `"unknown"`, never empty.
- The upstream's model name MUST NOT appear in any success body. It may appear inside a preserved upstream error text (7.2).
- `count_tokens` ignores `model`; route selection is the router's.

Why: the client keeps the string for display, usage tables and per-model feature decisions (context window, thinking support); a different name changes those decisions.

---------------------------------------------------------------------------------------------------

## 5. Tool ids and tool names

### 5.1 Ids (D5; emission rules in 02 "Tool calls" and 03 "Tool calls")

| Rule | Detail |
|---|---|
| Keep | An upstream id matching `^[a-zA-Z0-9_-]{1,64}$` that no earlier tool_use of the same message uses. |
| Synthesize | Everything else (missing, empty, illegal characters, longer than 64, duplicate within the message): `toolu_` + 24 characters from `[0-9A-Za-z]`, cryptographically random, distinct within the message. Never positional (`call_0`), never derived from the upstream id. |
| History | Ids the client echoes back are passed through the same per-request deterministic map (01 section 7.3) for the assistant `tool_calls[].id` and the tool message `tool_call_id`: an id matching the pattern that no earlier block claimed maps to itself, any other id hashes to `toolu_` + 24 hex. No state. |

Why: ids are stored in the client's transcript permanently. A vendor-style id (`functions.Bash:0`, ids containing `.`, `:` or `|`, empty ids, a repeated `call_0`) fails the id pattern that Messages-validating backends enforce, so any later request that carries it to such a backend (after a model switch or a different route) is rejected and the session is permanently poisoned; duplicate ids within a message break the client's tool_use / tool_result pairing. History ids pass through the same map because both halves of a pair come from the same transcript and the translation is deterministic: already-mapped ids (`toolu_` + 24 hex, emitted by this bridge in an earlier turn) match the pattern and map to themselves, while foreign ids hash exactly as 01 section 7.3 specifies. Synthesized ids are 30 characters, below the length caps some Chat vendors place on tool-call ids.

### 5.2 Names (D6; algorithm in 01 "Tool-name map")

- The per-request bidirectional map is built from the request's tools and from tool_use names present in history, and is derived from the request alone.
- The reverse map is applied to every tool_use name in the reply (stream and non-stream); a name not in the map is emitted unchanged; matching is exact (ordinal, case-sensitive).

Why: Chat vendors enforce `^[a-zA-Z0-9_-]{1,64}$` and answer 400 otherwise, while Claude Code's tool names include MCP names (`mcp__<server>__<tool>`) that can contain other characters (dots, colons, spaces from server names) or exceed 64 characters. The client executes tools by their exact original name; an unmapped name yields "tool not found" results and a stalled loop. History-only names (a tool filtered out of `tools`, or deferred) must be in the map so that a later reply naming them is still restored. Stateless derivation makes client retries and resumed sessions behave identically.

---------------------------------------------------------------------------------------------------

## 6. Reasoning round trip (D7; mapping in 01 "Reasoning controls" / "Vendor compat profiles", 02 "Reasoning", 03 "Reasoning")

### 6.1 Default (profile `none`)

| Direction | Rule |
|---|---|
| Reply | Upstream reasoning text becomes exactly one `thinking` block per message (stream: `thinking_delta` pieces that concatenate to the same text) with `signature` `""`; encrypted or redacted reasoning is dropped; reasoning blocks precede text and tool_use. |
| History | `thinking` and `redacted_thinking` blocks are dropped; an assistant message left empty by that is removed. |
| Effort | `reasoning_effort` only as 01 specifies (off by default). |

Why history is dropped by default: the client re-sends its thinking blocks with empty signatures, and Chat upstreams have no accepted place for them; the bridge cannot validate or sign them.

### 6.2 Vendor profile needs (selected only by the explicit provider option; never by host sniffing)

| Profile | What the upstream family requires | Consequence of using `none` instead |
|---|---|---|
| `deepseek` | In thinking mode every assistant message of the current tool-calling turn must carry its `reasoning_content` verbatim on the follow-up request; thinking switched by a `thinking` object plus a coarse effort. | The second request of a tool loop is rejected with 400. |
| `minimax` | Request flag `reasoning_split`; the assistant history must replay `reasoning_details`. | Interleaved-thinking quality degrades or the turn is rejected. |
| `mimo` | `reasoning_content` replay for assistant messages; thinking switched by a `thinking` object. | Same failure class as `deepseek`. |
| `zhipu` | Thinking switched by a `thinking` object plus an effort; history reasoning is not replayed. | Thinking is not enabled. |
| `none` | Upstreams that neither emit nor require reasoning, or tolerate its absence. | - |

Requirements that make re-injection possible:
- The reply MUST carry the complete reasoning text in order, unsummarised, before any text or tool_use, so the client's history holds the text that 01 later re-injects; several fragments of one message form one block (02 "Reasoning", 03 "Reasoning").
- Only thinking blocks the client actually holds are re-injected (01 joins several blocks of one message with `\n`). The bridge MUST NOT fabricate reasoning, and MUST NOT re-inject signatures or `redacted_thinking`.

Risk: a thinking block with an empty signature is displayed by the client but would be rejected by a real Messages backend that validates signatures if a session is later continued there (also recorded in 02).

---------------------------------------------------------------------------------------------------

## 7. Errors

### 7.1 Envelope and status table (D10)

Every failure reaches the client as `{"type":"error","error":{"type":T,"message":M}}` (a top-level `request_id` MAY be added and then MUST equal the `request-id` header) with the HTTP status below; no other body shape is permitted. Upstream statuses are kept unless a row says otherwise (same table as 02 "Errors").

| Upstream status | HTTP status returned | `error.type` |
|---|---|---|
| 400 | 400 | `invalid_request_error` |
| 401 | 401 | `authentication_error` (message per 7.3) |
| 403 | 403 | `permission_error` (message per 7.3) |
| 404 | 404 | `not_found_error` |
| 408 | 408 | `timeout_error` |
| 413 | 413 | `request_too_large` |
| 429 | 429 | `rate_limit_error` |
| 500, 502 | unchanged | `api_error` |
| 503, 529 | unchanged | `overloaded_error` |
| 504 | 504 | `timeout_error` |
| any other 4xx (402, 405, 409, 422, ...) | unchanged | `invalid_request_error` |
| any other 5xx (501, 505, 520-527, ...) | unchanged | `api_error` |
| status below 400 (unexpected redirect) | 502 | `api_error` |
| 2xx body that is an in-band error (7.6) | per 7.6 | per 7.6 |

Context-length overflow (7.5) overrides the table. Stream-phase failures are `event: error` frames with the same envelope (03 "Failure and termination").

### 7.2 Message text

Source, first available: `error.message` (string); `error` (string); `message` (string); `detail` (string, or a list whose string elements and `msg` members are joined with `"; "`); `errors[0].message`; for a non-JSON body, the body text. Truncate to 2,000 characters (UTF-16 code units, never splitting a surrogate pair; no marker). Empty or unusable -> `Upstream returned HTTP <status> with no error message.`; an HTML document (first non-whitespace character `<`) -> `Upstream returned an HTML error page (HTTP <status>).` (never echoed). The message MUST NOT contain the configured provider key (replace any occurrence with `***`), upstream URLs or internal identifiers. Identical to 02 ("Message") except for 7.3 and 7.5.

### 7.3 Upstream credential failures (401, 403)

The router's own client-authentication failures use the router's wording and never start with "The upstream provider". A rejection by the upstream is a different fault (the provider key configured on the route), and the client would otherwise present it as its own credential failure. Therefore:

| Upstream | Type / status | Message |
|---|---|---|
| 401 (or in-band authentication error) | `authentication_error`, 401 | `The upstream provider rejected the API key configured for this route (HTTP 401): <upstream message>` |
| 403 (or in-band permission error) | `permission_error`, 403 | `The upstream provider denied access for the API key configured for this route (HTTP 403): <upstream message>` |

`<upstream message>` is the 7.2 text, truncated so the whole message stays within 2,000 characters; when 7.2 produced only the generic "no error message" text the `: <upstream message>` suffix is omitted. For in-band errors without a numeric status the ` (HTTP nnn)` part is omitted.

Risk: the client treats a 401 `authentication_error` as a failure of its own credentials (it may re-run its key helper or prompt for login) even though the prefix names the upstream; the alternative (HTTP 502 `api_error`) would avoid that but is excluded by D10's "status kept".

### 7.4 Retry headers

`retry-after` (non-negative integer seconds or an HTTP date) and `retry-after-ms` (non-negative integer) from the upstream response are forwarded unchanged on any error response; invalid values are dropped. Nothing is synthesized. No other upstream header is forwarded (rate-limit, request-id, cookie, server, processing-time headers).

### 7.5 Context-length overflow

This section is the authoritative definition of overflow re-emission (D10 and 02 / 03 refer here).

#### 7.5.1 Detection

An error is an overflow when (a) the upstream status is 400, 413 or 422, or it is an in-band error (7.6) whose numeric status, if any, is not 429; and (b) at least one holds:
- `error.code` or `error.type` (case-insensitive) is `context_length_exceeded`, `context_window_exceeded` or `exceed_context_size_error`;
- the extracted message (7.2) contains, case-insensitively, any of: `maximum context length`, `context length`, `context window`, `context size`, `prompt is too long`, `prompt too long`, `input is too long`, `too many tokens`, `token limit`, `exceeds the maximum number of tokens`, `reduce the length of the messages`, `n_ctx`.

A 413 without any of these is a byte-size error and stays `request_too_large`.

#### 7.5.2 Number extraction

Quantities: I = input (prompt) tokens, M = requested completion tokens, L = context limit, T = total requested tokens. Numbers are decimal digits with optional `,` or `_` thousands separators (removed) and MUST fit in 2^53. Sources are tried in this order: numeric members `n_prompt_tokens` (I) and `n_ctx` (L) of the error object; then the extracted message; then the raw body text. The first phrasing that matches wins (case-insensitive; `...` = any text):

| # | Phrasing | Quantities |
|---|---|---|
| P1 | `input length and ` + optional quote/backtick + `max_tokens` + optional quote/backtick + ` exceed context limit: I + M > L` | I, M, L |
| P2 | `prompt is too long: I tokens > L maximum` | I, L |
| P3 | `maximum context length is L tokens` or `... of L tokens` or `context window is L tokens`, together with `(I in the messages, M in the completion)`, `(I in your prompt; M for the completion)`, `(I of text input, M in the output)` or `I tokens from the input messages and M tokens for the completion` | I, M, L |
| P4 | `maximum context length is L tokens` together with `you requested T tokens` (no split) | T, L |
| P5 | `maximum context length is L tokens` together with `your messages resulted in I tokens` / `your prompt has I tokens` | I, L |
| P6 | `The input length is I tokens and max_tokens is M` together with `maximum context window is L tokens` | I, M, L |
| P7 | `'max_tokens' or 'max_completion_tokens' is too large: M` together with `maximum context length is L tokens and your request has I input tokens` | I, M, L |
| P8 | `input token count (I) exceeds the maximum number of tokens allowed (L)` | I, L |
| P9 | `exceeded model token limit: L (requested: T)` | T, L |
| P10 | `Trying to keep the first I tokens ... context length of only L tokens` | I, L |
| P11 | `n_prompt_tokens` and `n_ctx` members present (no message pattern needed) | I, L |

The list MUST be implemented as a table (data), and SHOULD be extensible through configuration without code changes.

#### 7.5.3 Validation and form selection

Numbers are discarded unless consistent: (I, M, L) requires I + M > L; (I, L) requires I > L; (T, L) requires T > L; all values positive. Then:

| Available (after validation) | Output form |
|---|---|
| I, M, L with I <= L and L - I >= 4000 | Form B |
| I, M, L with I <= L and L - I < 4000 | Form A with N = I + M |
| I, M, L with I > L | Form A with N = I |
| I, L (I > L) | Form A with N = I |
| T, L | Form A with N = T |
| anything else (L unknown, no numbers, inconsistent numbers) | Form A-text |

Output texts (ASCII backticks and single spaces exactly as written; integers in plain decimal without separators):
- Form B: ``input length and `max_tokens` exceed context limit: <I> + <M> > <L>``
- Form A: `prompt is too long: <N> tokens > <L> maximum`
- Form A-text: `prompt is too long: <upstream message>` (the 7.2 text; truncated so the whole message stays within 2,000 characters; if that text already begins with `prompt is too long` no second prefix is added).

HTTP status 400 and type `invalid_request_error` in every case, whatever the original status (413 and 422 become 400). The rule applies identically to pre-stream HTTP errors, to non-stream in-band errors and to stream error frames (03). A reply already in Form A or Form B is re-rendered from its parsed numbers, so the selection rule applies to it too.

Why two forms: the client recognises both. Form B makes it re-send the same conversation with a smaller `max_tokens` (it computes roughly L - I minus a safety margin of 1,000, with a floor of 3,000, and gives up and shows the error when there is less room than the floor); Form A makes it compact the conversation (reactive compaction) and retry, using the stated numbers to decide how much history to drop. Form B is therefore chosen only when lowering `max_tokens` can succeed (L - I >= 4000); otherwise compaction is the useful recovery. With no usable numbers the prefix alone still selects compaction; the free-text tail keeps the upstream's wording visible to the user.

### 7.6 In-band errors, mid-stream failures and bridge-originated failures

| Condition | Result |
|---|---|
| Stream: in-band error object or upstream `event: error` | `event: error` frame, then the stream ends; type per 03 ("In-band error objects"); overflow per 7.5; authentication / permission wording per 7.3. |
| Stream: transport failure, idle timeout, EOF without `finish_reason` and without `[DONE]`, size limits, invalid tool arguments, nameless sole tool call | `event: error` frame, type `api_error`, messages as in 03 ("Mid-stream failures"). |
| Non-stream: 2xx body that is an in-band error | per 02 ("In-band errors on a 2xx reply"): status = integer `error.code` / `error.status` in 400-599, else 502; type from 7.1; message 7.2; overflow 7.5; credential wording 7.3. |
| Non-stream: 2xx body not a Chat completion object | 502 `api_error`, `Upstream returned a response that is not a Chat Completions object.` |
| No connection (refused, DNS, TLS, reset before a response) | 502 `api_error`, `Failed to reach the upstream provider.` No retry (D21). |
| No response or body within the router's per-provider timeout (D21) | 504 `timeout_error`, `Upstream request timed out.` For streams the header-phase timeout is this HTTP error; a mid-stream timeout is an error frame (03). |
| Unrepairable tool arguments / nameless sole tool call (non-stream) | 502 `api_error`, messages per 02 ("Tool calls"). |
| Request rejected by 01 | 400 `invalid_request_error`, message per 01 ("Validation and rejection list"). |
| Unexpected internal failure of the bridge | 500 `api_error`, `Internal error in the Messages bridge.` (no detail). |
| Client disconnected | nothing is written; the upstream request is cancelled promptly. |

### 7.7 Client-facing semantics this design relies on (verify per Open items)

| Response | How the client treats it |
|---|---|
| 408, 409, 429, any 5xx, `overloaded_error` (HTTP 529, or the type string in the envelope) | retried with exponential backoff (honouring `retry-after`); repeated overload may switch to a configured fallback model |
| 400 with Form B text | re-sent with a smaller `max_tokens` |
| 400 with `prompt is too long` | conversation compacted, then re-sent |
| other 4xx (401, 403, 404, 413, 422, ...) | not retried; message shown. 401 is read as an invalid own key; 404 `not_found_error` is read as "model unavailable" |
| stream `event: error` with `overloaded_error` | treated like HTTP 529 (retried) |
| stream `event: error` with `api_error` | surfaced to the user; not reliably retried |

Consequences: conditions that deserve a client retry (no connection, timeout, malformed upstream reply, upstream overload) MUST map to 5xx / 429 / `overloaded_error`; conditions that cannot succeed unchanged (validation, overflow, unsupported tool definitions) MUST map to 4xx. An upstream 429 whose text speaks of overload stays `rate_limit_error` (both are retried); 503 and 529 both yield `overloaded_error` with the upstream status kept.

Risk (D21): the bridge never retries, so every client retry (up to about ten identical re-sends on 429/5xx) is a new billed upstream request; an upstream that answers deterministic request defects with 500 makes the client retry pointlessly before showing the error.

---------------------------------------------------------------------------------------------------

## 8. Startup, background and utility calls

All of these are ordinary `POST /v1/messages` (or `count_tokens`) requests; none receives special routing. Table lists what the bridge must be prepared for.

| Call | Typical shape | Bridge handling |
|---|---|---|
| Startup connectivity / credential probe | non-stream, small-fast-tier model name, `max_tokens` 1, one-word user message, no tools | Floor to 16 (3.2); normal mapping; upstream failures surface as in section 7. Not answered locally. |
| Conversation title / topic detection | non-stream, small-fast model, small `max_tokens`, system prompt asking for a JSON object | Normal mapping. A reply that is not valid JSON is the client's concern. |
| Shell-command prefix / permission classification | non-stream, small-fast model, tiny `max_tokens`, short prompt | Normal mapping. |
| Web-page summarisation (WebFetch) | non-stream, small-fast model, large user text | Normal mapping; may overflow small-window upstreams (7.5). |
| WebSearch sub-request | typed server tool definition in `tools` | HTTP 400 naming the tool (01); the tool fails visibly. Users who need it disabled deny the tool in the client's permissions. |
| Compaction (manual and automatic) | main model, stream, very large input, final user message instructs a summary, large `max_tokens` | Normal streaming path; overflow here is the main trigger for Form A / Form B (7.5). |
| Context breakdown / token budgeting | several concurrent `count_tokens` calls with partial bodies (system only, tools only, messages only, a one-message baseline) | Served locally; estimator additivity (1.4.4) keeps differences meaningful; no I/O. |
| Sub-agents and background tasks | same endpoint, other model aliases and tool sets, concurrent | No shared state between requests; every request independent. |
| Client retry | identical body re-sent after 408/409/429/5xx or a dropped connection | Treated as a new request; output differs only in synthesized ids (D3, D5). |
| Interrupt (Esc) | client closes the connection mid-stream | Upstream request cancelled promptly; nothing further written (03). |
| Streaming-to-non-streaming fallback | same request re-sent with `stream:false` after a failed stream | Non-stream path MUST accept the same large `max_tokens` and long generations; deadline is the router's per-provider timeout (D21). |
| Models list, bootstrap, telemetry, OAuth | not sent to a bridged route, or answered by the router | Section 1.1. |

---------------------------------------------------------------------------------------------------

## 9. Not implemented by the bridge

- Retries, fallback chains, circuit breakers, concurrency queues, idempotency keys (D21; the client retries).
- Forwarding or deriving any caller header; per-vendor sniffing by host name (D20, D7).
- Hosted web search, server tools, citations, files / batches / skills APIs, PDF input (D13, D16).
- Local model discovery, bootstrap, OAuth / federation endpoints, telemetry endpoints (router or client side).
- Rewriting the reply model name beyond echoing the inbound one; exposing upstream ids, models or headers.
- Billing, usage ledgers, key quotas, request logs of bodies.

---------------------------------------------------------------------------------------------------

## 10. Implementation requirements (.NET)

- JSON via a document model; no reflection serialization; NativeAOT-safe; no dynamic code generation.
- The estimator walks the parsed body once; it MUST NOT copy large strings (base64 images and files are counted by length class without materialising copies where avoidable) and MUST NOT throw on any input.
- The outbound header set is constructed from empty and filled from the allowlist only (2.2); a test MUST fail if any other header can appear.
- Random ids (`msg_`, `toolu_`) come from a cryptographically secure generator.
- Error construction is a single function from (status, headers, body text or in-band object) to (status, headers, envelope); overflow patterns are data.
- Logs never contain request or reply bodies, credentials or the attribution line; bodies of rejected requests are not logged.
- No shared mutable state between requests (thread-safe by construction).

---------------------------------------------------------------------------------------------------

## 11. Edge-case matrix (TARGET results)

Notation: "env" = the Messages error envelope of 7.1; `S` = HTTP status. Estimator vectors use the algorithm of 1.4.

### 11.1 Endpoints and headers

| ID | Input | Result |
|---|---|---|
| E01 | `POST /v1/messages?beta=true`, valid streaming body | Bridged exactly as without the query; the upstream URL carries no query. |
| E02 | `GET /v1/messages` | S 405, env `invalid_request_error` `Method Not Allowed`, `allow: POST`. |
| E03 | `GET /v1/messages/count_tokens` | S 405 as above. |
| E04 | `POST /v1/messages/batches` | S 404, env `not_found_error`, `Not found: POST /v1/messages/batches`. |
| E05 | `GET /v1/files` | S 404, env `not_found_error`, `Not found: GET /v1/files`. |
| E06 | `POST /api/event_logging/batch` | S 404, env `not_found_error`. |
| E07 | Client sends `anthropic-version: 2099-01-01` and `anthropic-beta: a,b,c` | Accepted; no effect; neither header on the upstream request. |
| E08 | Client sends neither `anthropic-version` nor `anthropic-beta` | Accepted. |
| E09 | Streaming bridged request, outbound headers | Exactly `content-type: application/json`, `accept: text/event-stream`, the managed credential header (`authorization: Bearer <key>`, or the provider's relocated header name), plus the router-stamped session header if present; nothing else beyond transport headers. |
| E10 | Non-stream bridged request, outbound `accept` | `application/json`. |
| E11 | Client sends `cookie`, `user-agent: claude-cli/2.1.207 (external, cli)`, `x-app: cli`, `x-stainless-lang: js`, `x-api-key: k`, `anthropic-dangerous-direct-browser-access: true`, `x-claude-code-session-id: s1`, `accept: application/json`, `accept-encoding: gzip` | None of them on the upstream request (the router's stamped session header, if any, aside). |
| E12 | Router stamped `x-opencode-session: abc` | Present once, value `abc`, unchanged. |
| E13 | Non-stream 200 | `content-type: application/json`; no upstream header relayed. |
| E14 | Upstream 429 with `retry-after: 7`, `retry-after-ms: 7000`, `x-ratelimit-remaining-tokens: 0`, `set-cookie: a=b` | S 429, env `rate_limit_error`; response carries `retry-after: 7` and `retry-after-ms: 7000` only. |
| E15 | Upstream 429 with `retry-after: soon` | Header dropped (invalid). |
| E16 | Upstream 503 without retry headers | No retry header synthesized; no `x-should-retry`. |

### 11.2 count_tokens and estimator

| ID | Input | Result |
|---|---|---|
| T01 | `{"model":"m","messages":[{"role":"user","content":"hello"}]}` | 200 `{"input_tokens":9}` (Ureq 12+16+5 = 33). No upstream request. |
| T02 | T01 with `?beta=true` | Same. |
| T03 | `messages` missing | 400 env `messages: Field required`. |
| T04 | `messages` is a string | 400 env `messages: Input should be a valid list`. |
| T05 | `messages: []`, nothing else | 200 `{"input_tokens":3}`. |
| T06 | Body is `[]` | 400 env `Request body must be a JSON object.` |
| T07 | T01 + `system` string `You are Claude Code.` | Ureq 33 + 21 = 54 -> `{"input_tokens":14}`. |
| T08 | T01 + `system` list [attribution block, `{"type":"text","text":"You are Claude Code."}`] | Attribution block ignored: 14 (same as T07). |
| T09 | T01 + tool `{"name":"Read","description":"Read a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}` | Tool = 48 + 4 + 11 + 108 = 171; Ureq 204 -> `{"input_tokens":51}`. |
| T10 | `content` `"你好世界"` | Ureq 12+16+24 = 52 -> 13. |
| T11 | one `image` block (any size) | Ureq 12+16+6000 -> 1507. |
| T12 | user "read it"; assistant `tool_use` Read `{"path":"/a/b.txt"}`; user `tool_result` "file contents" | Ureq 161 -> 41. |
| T13 | history with an assistant `thinking` block of 10,000 characters | Contributes 0. |
| T14 | user content [text "hello", `document` block] | Document counted as its placeholder `[omitted: unsupported content block 'document']` (52 units): Ureq 85 -> 22; no rejection. |
| T15 | `tools` contains `{"type":"web_search_20250305","name":"web_search"}` | Contributes 0; 200 (no 400 here, unlike `/v1/messages`). |
| T16 | `tools` is a string, `system` is a number | Both ignored; 200. |
| T17 | Add `{"role":"user","content":"hi"}` to T01 | Ureq rises by exactly 18 units (additivity). |
| T18 | `count_tokens` body without `model` | Served normally (the router decides routing). |
| T19 | `EST("")`, `EST("Hello, world.")`, `EST("😀")` | 0, 4 (15 units), 2 (8 units). |
| T20 | `count_tokens` while the upstream is down | Unaffected: 200. |

### 11.3 Estimator as usage fallback (with 02 / 03)

| ID | Input | Result |
|---|---|---|
| F01 | Stream for the T01 body: `message_start` | `usage.input_tokens` 9, `output_tokens` 1 (D8, 03). |
| F02 | Non-stream, upstream sends no usage; reply text `Hello! How can I help?` | `output_tokens` 6 (24 units); `input_tokens` 9 for the T01 request; cache counters 0. |
| F03 | No usage; one emitted tool_use `Read` with input `{"path":"/a/b.txt"}` | S = `Read` + `{"path":"/a/b.txt"}` = 33 units -> `output_tokens` 9. |
| F04 | Upstream usage with `prompt_tokens` 0 and `completion_tokens` 10 | Prompt treated as absent: input from the request estimate (02 "Usage"). |
| F05 | Thinking `Let me think.` (14 units) and text `Done.` (6 units) emitted, no usage | `EST` over the concatenation: 20 units -> `output_tokens` 5. |

### 11.4 Attribution block and output floor

| ID | Input | Result |
|---|---|---|
| A01 | `system` [attribution, B, C] | Attribution dropped; system message = B + `\n` + C (01). |
| A02 | `system` [attribution] only | No system message. |
| A03 | `system` [B, attribution] | Untouched (not first). |
| A04 | `system` string beginning `x-anthropic-billing-header: ...` | Untouched. |
| A05 | First block text `  x-anthropic-billing-header: ...` (leading space) | Not recognised; kept. |
| A06 | First block text `X-Anthropic-Billing-Header: cc_version=...` | Recognised (case-insensitive); dropped. |
| A07 | First block text `x-anthropic-billing-header: ...;\nExtra instructions` | Whole block dropped. |
| A08 | First element is an `image` block, second the attribution line | Nothing dropped. |
| M01 | `max_tokens` 1 | Upstream limit 16. |
| M02 | 15 / 16 / 17 / 4096 | 16 / 16 / 17 / 4096. |
| M03 | `max_tokens` 0 | 400 env `max_tokens: Input should be a valid integer greater than or equal to 1` (01). |
| M04 | Probe (limit 1) answered by the upstream with 12 tokens and `finish_reason` `length` | Client receives all 12 tokens; `stop_reason` `max_tokens`; usage as reported. |
| M05 | Probe answered with `finish_reason` `stop` | `stop_reason` `end_turn`. |

### 11.5 Model name

| ID | Input | Result |
|---|---|---|
| N01 | Inbound `my-alias[1m]` | `message.model` and `message_start.message.model` = `my-alias[1m]`. |
| N02 | Inbound `Provider/Model-X` | Echoed unchanged. |
| N03 | Upstream reports `gpt-4o-2024-08-06` | Never exposed. |
| N04 | Upstream omits `model` | Inbound name still echoed. |
| N05 | Upstream error text mentions its own model | Preserved in the error message. |

### 11.6 Tool ids and names

| ID | Input | Result |
|---|---|---|
| I01 | Upstream tool-call id `call_abc123` | Kept. |
| I02 | `functions.Bash:0` | `toolu_` + 24 `[0-9A-Za-z]`. |
| I03 | Missing id / empty string | Synthesized. |
| I04 | Two calls both `call_1` | First kept; second synthesized. |
| I05 | Id of exactly 64 legal characters / 65 characters | Kept / synthesized. |
| I06 | History carries a client-echoed id `functions.Bash:0` | Passed through the per-request map (01 section 7.3): `toolu_` + 24 hex of SHA-256, same as T28 in 01. |
| I07 | Tool `mcp__srv.name__do-it` | Upstream name `mcp__srv_name__do-it`; a reply naming it yields the original `mcp__srv.name__do-it` (stream and non-stream). |
| I08 | 78-character tool name `mcp__averyverylongserver__averyverylongtoolname_that_goes_past_the_limit_of_64` | Upstream name `mcp__averyverylongserver__averyverylongtoolname_that_go_3609e2a4` (64 characters); restored on the reply. |
| I09 | Tools `a_b` then `a.b` | `a_b` and `a_b_2e7336dc`; each restored to its own original. |
| I10 | Name occurs only in a history tool_use (tool no longer in `tools`) | In the map; restored. |
| I11 | Reply names `Bash`, not in the map | Emitted unchanged. |
| I12 | Reply names `read`, map has `Read` | Emitted as `read` (case-sensitive). |
| I13 | Tool name `a..b` | Upstream name `a_b` (one `_` per illegal run). |

### 11.7 Reasoning

| ID | Input | Result |
|---|---|---|
| R01 | Profile `none`, reply `reasoning_content` `think` then text | `thinking` block `{"type":"thinking","thinking":"think","signature":""}` before the text block. |
| R02 | History assistant message with thinking text and a tool_use | Thinking dropped; tool_calls kept. |
| R03 | History assistant message that is thinking only | Removed. |
| R04 | Profile `deepseek`, history assistant [thinking `A`, tool_use] | `reasoning_content` `A` on that message; two blocks `A`, `B` -> `A\nB`. |
| R05 | Profile `minimax`, one thinking block `A` | `reasoning_details` `[{"type":"reasoning.text","index":0,"text":"A"}]`; request has `reasoning_split` true. |
| R06 | `redacted_thinking` in history, any profile | Never re-injected. |
| R07 | Upstream returns encrypted reasoning only | No thinking block; no error. |
| R08 | Upstream host looks like a DeepSeek host but `compatProfile` is unset | Profile `none` applies. |

### 11.8 Errors (status, type, text)

| ID | Upstream | Result |
|---|---|---|
| X01 | 400 `{"error":{"message":"bad parameter x"}}` | S 400, `invalid_request_error`, `bad parameter x`. |
| X02 | 401 `{"error":{"message":"Incorrect API key provided: sk-abc***xyz"}}` | S 401, `authentication_error`, `The upstream provider rejected the API key configured for this route (HTTP 401): Incorrect API key provided: sk-abc***xyz`. |
| X03 | 401 with empty body | S 401, `The upstream provider rejected the API key configured for this route (HTTP 401)`. |
| X04 | 403 `{"message":"forbidden"}` | S 403, `permission_error`, `The upstream provider denied access for the API key configured for this route (HTTP 403): forbidden`. |
| X05 | 401 whose text contains the configured key | Key replaced with `***`. |
| X06 | 404 `{"error":{"message":"The model `x` does not exist"}}` | S 404, `not_found_error`, text preserved. |
| X07 | 408 | S 408, `timeout_error`. |
| X08 | 413 `Request body too large` (no token wording) | S 413, `request_too_large`. |
| X09 | 429 rate-limit text mentioning `token limit`, code `rate_limit_exceeded` | S 429, `rate_limit_error` (429 is never an overflow). |
| X10 | 500 | S 500, `api_error`. |
| X11 | 502 `Bad Gateway` (plain text) | S 502, `api_error`, `Bad Gateway`. |
| X12 | 502 with an HTML page | S 502, `api_error`, `Upstream returned an HTML error page (HTTP 502).` |
| X13 | 503 | S 503, `overloaded_error`. |
| X14 | 504 | S 504, `timeout_error`. |
| X15 | 529 | S 529, `overloaded_error`. |
| X16 | 522 | S 522, `api_error`. |
| X17 | 402 | S 402, `invalid_request_error`. |
| X18 | 422 `{"detail":[{"loc":["body","messages"],"msg":"field required"}]}` | S 422, `invalid_request_error`, `field required`. |
| X19 | 500 with empty body | S 500, `Upstream returned HTTP 500 with no error message.` |
| X20 | 400 with a 2,500-character message | Message cut to 2,000 characters, no marker. |
| X21 | 302 redirect | S 502, `api_error`. |
| X22 | 200 non-stream body `{"error":{"message":"rate limit","code":429}}` | S 429, `rate_limit_error` (02). |
| X23 | Connection refused | S 502, `api_error`, `Failed to reach the upstream provider.`; one attempt only. |
| X24 | No response within the router's provider timeout | S 504, `timeout_error`, `Upstream request timed out.` |
| X25 | Stream: after partial text, `data: {"error":{"message":"Server overloaded","type":"server_error","code":"overloaded"}}` | `event: error`, `overloaded_error` (03 classification), stream ends. |
| X26 | Stream: in-band `code` `rate_limit_exceeded` | `event: error`, `rate_limit_error`. |
| X27 | Stream: in-band unknown error | `event: error`, `api_error`. |
| X28 | Stream: in-band overflow text | `event: error`, `invalid_request_error`, message per 7.5. |
| X29 | Stream request, upstream answers HTTP 400 before any data | Plain JSON error (not SSE), S 400. |
| X30 | Client authentication failure inside the router | Router wording; not produced by the bridge and never prefixed `The upstream provider`. |
| X31 | Request rejected by 01 (for example a typed server tool definition) | S 400 `invalid_request_error`, message naming the tool (01); no upstream request. |
| X32 | Unexpected exception in the bridge | S 500 `api_error` `Internal error in the Messages bridge.`; no detail leaked. |

### 11.9 Context-length overflow

| ID | Upstream error text (status 400 unless noted) | Result (400 `invalid_request_error`) |
|---|---|---|
| O01 | `This model's maximum context length is 8192 tokens. However, you requested 9000 tokens (5000 in the messages, 4000 in the completion). Please reduce the length of the messages or completion.` | I=5000 M=4000 L=8192, L-I = 3192 < 4000 -> `prompt is too long: 9000 tokens > 8192 maximum` |
| O02 | `Requested token count exceeds the model's maximum context length of 262144 tokens. You requested a total of 262462 tokens: 230462 tokens from the input messages and 32000 tokens for the completion.` | L-I = 31682 -> ``input length and `max_tokens` exceed context limit: 230462 + 32000 > 262144`` |
| O03 | `litellm.BadRequestError: ContextWindowExceededError: This model's maximum context length is 4097 tokens, however you requested 5360 tokens (4851 in your prompt; 509 for the completion).` | I > L -> `prompt is too long: 4851 tokens > 4097 maximum` |
| O04 | `This model's maximum context length is 128000 tokens. However, your messages resulted in 150000 tokens.` | `prompt is too long: 150000 tokens > 128000 maximum` |
| O05 | `ContextWindowExceededError: maximum context window is 131072 tokens. The input length is 130000 tokens and max_tokens is 4096.` | L-I = 1072 -> `prompt is too long: 134096 tokens > 131072 maximum` |
| O06 | `{"error":{"code":400,"message":"the request exceeds the available context size, try increasing it","type":"exceed_context_size_error","n_prompt_tokens":5000,"n_ctx":4096}}` | `prompt is too long: 5000 tokens > 4096 maximum` |
| O07 | `The input token count (1100000) exceeds the maximum number of tokens allowed (1048575).` | `prompt is too long: 1100000 tokens > 1048575 maximum` |
| O08 | `Your request exceeded model token limit: 262144 (requested: 270000)` | `prompt is too long: 270000 tokens > 262144 maximum` |
| O09 | `Trying to keep the first 6000 tokens when context overflows. However, the model is loaded with context length of only 4096 tokens, which is not enough.` | `prompt is too long: 6000 tokens > 4096 maximum` |
| O10 | `'max_tokens' or 'max_completion_tokens' is too large: 32000. This model's maximum context length is 8192 tokens and your request has 5000 input tokens (32000 > 8192 - 5000).` | L-I = 3192 -> `prompt is too long: 37000 tokens > 8192 maximum` |
| O11 | Same as O10 with 1000 input tokens | L-I = 7192 -> ``input length and `max_tokens` exceed context limit: 1000 + 32000 > 8192`` |
| O12 | `{"error":{"code":"context_length_exceeded","message":"Request too long."}}` | No numbers -> `prompt is too long: Request too long.` |
| O13 | `input is too long for this model` | `prompt is too long: input is too long for this model` |
| O14 | `prompt is too long: 213462 tokens > 200000 maximum` | Unchanged (re-rendered from I=213462, L=200000). |
| O15 | ``input length and `max_tokens` exceed context limit: 100000 + 8000 > 105000`` | L-I = 5000 -> unchanged Form B. |
| O16 | ``input length and `max_tokens` exceed context limit: 104000 + 8000 > 105000`` | L-I = 1000 -> `prompt is too long: 112000 tokens > 105000 maximum` |
| O17 | `maximum context length is 8192 tokens ... (100 in the messages, 50 in the completion)` | Inconsistent (150 <= 8192): numbers discarded -> `prompt is too long: <upstream message>` |
| O18 | `maximum context length is 8,192 tokens. However, you requested 9,000 tokens (8,000 in the messages, 1,000 in the completion).` | Separators removed; L-I = 192 -> `prompt is too long: 9000 tokens > 8192 maximum` |
| O19 | Overflow text with status 422 / status 413 | Re-emitted with S 400. |
| O20 | Overflow text on a 429 | Not an overflow: `rate_limit_error`, S 429. |
| O21 | Stream error frame carrying O02's text | `event: error`, `invalid_request_error`, Form B text. |
| O22 | 400 `max_tokens must be at most 8192` (completion cap, no context wording) | Not an overflow: `invalid_request_error` with the upstream text. |
| O23 | Message already starting `prompt is too long` but without numbers | Kept as is (no second prefix). |

### 11.10 Utility calls

| ID | Input | Result |
|---|---|---|
| U01 | Title call: non-stream, small model, `max_tokens` 512, JSON-asking system prompt | Normal mapping; 200 message; usage per D8. |
| U02 | WebSearch sub-request with the typed server tool definition | S 400, message naming the tool; no upstream request. |
| U03 | Compaction request whose input exceeds the upstream window | O-series results (7.5); status 400. |
| U04 | Identical request re-sent by the client after a 529 | Independent request; same mapping, different synthesized ids. |
| U05 | Client closes the connection mid-stream | Upstream cancelled; no further bytes. |
| U06 | Streaming failure, then the same body re-sent with `stream:false` and a large `max_tokens` (for example 20000) | Non-stream path serves it; deadline = router's provider timeout. |
| U07 | PDF read: history contains a `document` block | Placeholder text part `[omitted: unsupported content block 'document']` (01); request proceeds. |
| U08 | `count_tokens` bursts (5 concurrent partial bodies) | Each answered independently and deterministically. |

---------------------------------------------------------------------------------------------------

## 12. Open items (need live measurement or a product decision)

1. Verify the client constants this file relies on against the shipped CLI version: the two recognised overflow sentences, the safety margin (1,000) and floor (3,000) behind the L - I >= 4000 threshold, which statuses it retries, whether it still sends the `max_tokens` 1 probe, `count_tokens` and `?beta=true`.
2. Estimator calibration against live tokenizers per upstream family (a per-provider scale factor option may be needed); effect of under-estimation on compaction timing.
3. Whether a constant router-defined `user-agent` (or other static provider headers) must be allowed for upstreams whose edge layers or agent allowlists reject requests without one (2.2).
4. Whether upstream 503 should be returned as 529 so the client's overload-specific handling (fallback model) engages; currently D10 keeps the upstream status.
5. Whether `x-should-retry: false` should accompany quota-exhausted 429s and overflow errors to stop pointless client retries (currently the bridge sets no such header).
6. Exact vendor message corpora for P1-P11 per upstream, and additional phrasings seen in production (7.5.2 table is data and extensible).
7. Whether in-band stream errors that carry only a string code should also set a status class for non-stream replies (02 uses 502 `api_error` when no numeric status exists, whereas 03 classifies by code string).
8. Exact replay fields required by MiniMax, MiMo and DeepSeek on non-tool turns (01 Open items; the profile needs in 6.2 are the family-level minimum).
9. Whether the client displays an upstream-credential 401 differently from its own 401 (7.3 relies on the message text only).
10. Whether the client accepts thinking blocks with an empty signature in every version when a session continues on another route (02 risk).
