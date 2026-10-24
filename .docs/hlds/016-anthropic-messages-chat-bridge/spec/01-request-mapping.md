# 01 - Request mapping: Messages request -> Chat Completions request (normative)

Status: FINAL target specification. Language: MUST / SHOULD / MAY as in RFC 2119.
Siblings: `02-response-mapping.md` (Chat reply -> Messages reply), `03-stream-state-machine.md` (Chat SSE -> Messages SSE), `04-harness-and-errors.md` (client harness facts, token estimator, error envelope, outbound header allowlist, count_tokens).
Binding cross-cutting decisions (D1-D21) are referenced as "Dn".

Vocabulary: "bridge" = the component translating a Claude Code Messages request into a Chat Completions request and the reply back. "Client" = Claude Code. "Upstream" = the Chat-only provider. "Neutral item" = an intermediate text / image / tool-use / tool-result / reasoning item.

---------------------------------------------------------------------------------------------------

## 1. Contract and pipeline

Contract (D17): input = inbound Messages JSON string + inbound model name; output = Chat Completions JSON string + a request-derived context (section 14). The body model has already been renamed by the router; the bridge MUST copy `model` verbatim and MUST NOT map model names. Cache-breakpoint injection and tool-catalog ordering have already run; any `cache_control` they inserted is ignored here. A tool filter that understands Chat bodies runs after this mapping; the forwarder (credentials, headers) runs after that.

The mapper MUST execute these steps in this order and MUST report the first violation found (section 3 gives the order of checks):

1. Parse JSON; require an object.
2. Validate top-level scalar fields (model, max_tokens, stream, temperature, top_p, stop_sequences, system).
3. Classify and validate `tools`; build the tool-name map from the request tools (section 8.3).
4. Validate `tool_choice` against the surviving tools.
5. Validate and walk `messages`; extend the tool-name map with tool_use names found in history; convert messages (section 7).
6. Build `system` message (section 6) and place it first.
7. Compute sampling / limit / user fields (section 10) and streaming fields (section 11).
8. Compute reasoning controls (sections 12, 13).
9. Assemble the body in the canonical key order; emit the request-derived context.

Canonical key order of the body (semantically irrelevant, required for byte-stable output so that provider prefix caches stay warm): `model`, `messages`, `temperature`, `top_p`, `max_tokens` (or the configured alternative field), `stop`, `tools`, `tool_choice`, `parallel_tool_calls`, `stream`, `stream_options`, `thinking`, `reasoning_split`, `reasoning_effort`, `user`. Message key order: assistant = `role`, `content`, `tool_calls`, `reasoning_content`, `reasoning_details`; tool = `role`, `tool_call_id`, `content`; user and system = `role`, `content`. Tool object order: `type`, `function` { `name`, `parameters`, `description` }.

The output MUST be a pure function of (input body, inbound model name, provider options): identical inputs MUST produce byte-identical outputs.

---------------------------------------------------------------------------------------------------

## 2. Provider options

This table is the single authoritative list of per-provider options consumed by the mapper; other sections and other documents refer to options by these names and MUST NOT restate defaults. "Required by the bridge" = the bridge reads the option for every bridged provider and requires a valid value (the documented default applies when unset); "optional tuning" = off or neutral unless an operator opts in per provider.

| Option | Values | Default | Class | Purpose |
|---|---|---|---|---|
| `compatProfile` | `none`, `deepseek`, `minimax`, `mimo`, `zhipu` | `none` | required by the bridge | Selects the optional vendor layer (section 13): history reasoning re-injection and vendor thinking keys. When unset the default `none` applies; the mapper MUST NOT infer it from the upstream host. An unknown value is a startup configuration error. |
| `sendReasoningEffort` | boolean | `false` | optional tuning | Operator opt-in per provider for sending reasoning controls. When `false`, no reasoning control is sent at all: no `reasoning_effort` and no `thinking` key, under every profile (sections 12, 13). When `true`, the keys defined by the selected profile are sent. |
| `userIdMode` | `off`, `hash`, `passthrough` | `off` | optional tuning | How `metadata.user_id` becomes the Chat `user` field (section 10). `off` sends nothing. |
| `maxTokensField` | `max_tokens`, `max_completion_tokens` | `max_tokens` | optional tuning | Name of the output-limit key (section 10). |
| `maxOutputTokens` | integer >= 16, or unset | unset | optional tuning | Upper cap applied to the output limit (section 10). |
| `AnthropicUpstreamApi` | `messages`, `chat_completions` | `messages` | required (provider-level gate) | Turns bridging on for this provider; conventional env `<PROVIDER>_ANTHROPIC_UPSTREAM_API`. Only a `chat_completions` provider on a matched imposter route reaches this mapper. Validated at startup and on admin upsert: an unknown value fails. The effective auth scheme is orthogonal (D20): the forwarder writes the managed credential per the provider's `AuthScheme`/`AuthHeader`, so a `chat_completions` provider may use `Bearer` or `ApiKey`. |
| `forwardCacheControl` | boolean | `false` | optional tuning | Copies a block's `cache_control` object verbatim onto the corresponding Chat content part, tool message, tool definition or system part (sections 4 and 7). Only an upstream that understands Anthropic-style cache breakpoints benefits. |
| `sendResponseFormat` | boolean | `false` | optional tuning | Maps `output_config.format` to Chat `response_format` (sections 4 and 10). |
| `documentMode` | `placeholder`, `image` | `placeholder` | optional tuning | `placeholder` replaces an unsupported document with a text placeholder; `image` sends a base64 PDF or image document as an `image_url` data URL and a URL document as its URL, for a vision-capable upstream (sections 4 and 7.2). |
| `midturnSystem` | `system`, `merge_into_user` | `system` | optional tuning | How an in-sequence `system` message is mapped (section 7.0). `merge_into_user` is for an upstream whose chat template allows only a leading system message. |

Not options: streaming requests always carry `stream_options.include_usage = true` (D8); the output-limit floor of 16 always applies (D19); the bridge is enabled by the provider option above as a router-side gate (Anthropic-dialect provider whose upstream API is `chat_completions`, D17), not by a mapper option. Operator-facing documentation SHOULD reference this table rather than copy it.

---------------------------------------------------------------------------------------------------

## 3. Validation and rejection list

A rejected request MUST NOT reach the upstream. The response is HTTP 400 with the Messages error envelope (D10): `{"type":"error","error":{"type":"invalid_request_error","message":M}}`. Indices `{i}` (message), `{j}` (block within message content), `{k}` (tool, or block within tool_result content) are zero-based. `{type}` is the literal block or tool type string.

Order of checks (first violation wins): body, model, max_tokens, stream, temperature, top_p, stop_sequences, system, tools (in order), tool_choice, messages (in order; within a message, blocks in order; within a tool_result, nested blocks in order).

| # | Condition | Message M (exact) |
|---|---|---|
| R01 | body is not a JSON object | `Request body must be a JSON object.` |
| R02 | `model` missing or null / not a non-empty string | `model: Field required` / `model: Input should be a valid string` |
| R03 | `max_tokens` missing or null / not an integer >= 1 (a JSON number with zero fraction is an integer) | `max_tokens: Field required` / `max_tokens: Input should be a valid integer greater than or equal to 1` |
| R04 | `stream` present, not null, not boolean | `stream: Input should be a valid boolean` |
| R05 | `temperature` / `top_p` present, not null, not a number | `temperature: Input should be a valid number` / `top_p: Input should be a valid number` |
| R06 | `stop_sequences` not a list / an element not a string | `stop_sequences: Input should be a valid list` / `stop_sequences.{n}: Input should be a valid string` |
| R07 | `system` not a string or list | `system: Input should be a valid string or list` |
| R08 | `tools` not a list / element not an object | `tools: Input should be a valid list` / `tools.{k}: Input should be a valid dictionary` |
| R09 | custom tool (no `type`, or `type` = `custom`) without a non-empty string `name` | `tools.{k}.name: Field required` |
| R10 | `input_schema` present, not null, not an object | `tools.{k}.input_schema: Input should be a valid dictionary` |
| R11 | typed server TOOL DEFINITION (D13): any tool whose `type` is present, is not `custom`, and is not a tool-search type - e.g. `web_search_*`, `web_fetch_*`, `code_execution_*`, `computer_*`, `bash_*`, `text_editor_*`, `memory_*`, `mcp_toolset` | `tools.{k}.type: "{type}" server tools are not supported` |
| R12 | `tool_choice` is a string other than `auto`, `any`, `required`, `none`; or an object whose `type` is not `auto`, `any`, `tool`, `none` | `tool_choice: Input should be 'auto', 'any', 'tool' or 'none'` (string case) / `tool_choice.type: Input should be 'auto', 'any', 'tool' or 'none'` (object case); `tool_choice` of any other JSON kind (number, boolean, array) -> `tool_choice: Input should be a valid dictionary` |
| R13 | `tool_choice` type `tool` with missing / empty / non-string `name` | `tool_choice.name: Field required` |
| R14 | `tool_choice` type `tool` naming a tool that is not among the surviving tools (compared by original name) | `tool_choice.name: no tool named "{name}" in tools` |
| R15 | `tool_choice` type `any` / `required` / `tool` while no tool survives | `tool_choice: "{type}" requires at least one tool` (`{type}` = `any`, `required`, or `tool`) |
| R16 | `messages` missing or null / not a list / empty list | `messages: Field required` / `messages: Input should be a valid list` / `messages: at least one message is required` |
| R17 | message not an object / `role` not `user`, `assistant` or `system` (including `developer`, `tool`, missing) | `messages.{i}: Input should be a valid dictionary` / `messages.{i}.role: Input should be 'user', 'assistant' or 'system'` |
| R18 | message `content` missing or null / neither string nor list | `messages.{i}.content: Field required` / `messages.{i}.content: Input should be a valid string or list` |
| R19 | content element not an object / without a string `type` (also for the elements of a tool_result content list, path `messages.{i}.content.{j}.content.{k}`) | `messages.{i}.content.{j}: Input should be a valid dictionary` / `messages.{i}.content.{j}.type: Field required` |
| R20 | `tool_use` block (assistant) missing / empty `id`; missing / empty `name`; `input` present, not null, not an object | `messages.{i}.content.{j}.id: Field required` / `...name: Field required` / `messages.{i}.content.{j}.input: Input should be a valid dictionary` |
| R21 | `tool_result` block (user) missing / empty `tool_use_id`; `content` present, not null, neither string nor list | `messages.{i}.content.{j}.tool_use_id: Field required` / `messages.{i}.content.{j}.content: Input should be a valid string or list` |
| R22 | user `image` block: missing `source` / `source.type` not `base64` or `url` / base64 `media_type` not an image type / base64 `data` missing or empty / url `url` missing or empty | `messages.{i}.content.{j}.source: Field required` / `...source.type: Input should be 'base64' or 'url'` / `...source.media_type: Input should be an image media type` / `...source.data: Field required` / `...source.url: Field required` (for an image nested in tool_result, path `messages.{i}.content.{j}.content.{k}...`) |
| R23 | after all drops no message has usable content | `messages: no message contains usable content` |

Rules for R-checks:
- "Image media type" = a string that, ASCII-lower-cased, starts with `image/`.
- R11 applies to the tool's `type` only; a tool-search type (a `type` equal to `tool_search` or starting with `tool_search_tool_`) is not an error (section 4).
- R23 is evaluated after every message has been converted and after empty-message removal.
- Among the `tool_choice` checks the order is R12, R13, R15 (no tool survives), R14 (named tool absent).
- Rejection text MUST name the offending field path as shown; no further detail is required.
- Unsupported content BLOCKS are not errors: they are replaced by a placeholder (section 7.2). Only tool definitions (R11) are rejected for being unsupported.

Risk: R11 makes Claude Code's WebSearch tool fail loudly (a 400 on every search; the tool reports the error to the model). That is intended: the missing capability is visible instead of degrading to model-memory answers.

Risk: a placeholder replaces content the model never saw (for example a PDF); the model may answer as if it had read it or may ask the user to resend. See Open items.

---------------------------------------------------------------------------------------------------

## 4. Top-level field policy

The Chat body is an allowlist build. Fields not listed here MUST be dropped (debug log). Header `anthropic-beta` has no effect (D13).

| Field | Handling |
|---|---|
| `model` | Copied verbatim to `model`. |
| `messages` | Section 7. |
| `system` | Section 6. |
| `max_tokens` | Section 10 (floor 16, optional cap). |
| `temperature`, `top_p` | Copied verbatim when numbers (text of the number preserved). |
| `top_k` | Dropped. |
| `stop_sequences` | Section 10. |
| `stream` | Section 11. |
| `tools`, `tool_choice` | Sections 8, 9. |
| `thinking`, `output_config` | Section 12 (read for effort; `budget_tokens` has no direct Chat equivalent). Wrong types are ignored (debug log). `output_config.format` maps to Chat `response_format` when `sendResponseFormat` is `true` (section 10); dropped otherwise. |
| `metadata` | Only `metadata.user_id` is read, and only when `userIdMode` is not `off` (section 10). Wrong type ignored. |
| `service_tier`, `context_management`, `container`, `mcp_servers`, top-level `cache_control`, `betas`, `speed`, and any unknown field | Dropped. |
| `cache_control` (per block, per tool, per system part) | Dropped unless `forwardCacheControl` is `true`, in which case each `cache_control` object is copied verbatim onto the corresponding Chat content part, tool message, tool definition or system content part. |
| tool `strict` (boolean) | Forwarded verbatim as the Chat function object's `strict` member when it is a boolean; dropped otherwise. |
| Per-block / per-tool: `citations`, tool `defer_loading`, `eager_input_streaming`, `input_examples`, `allowed_callers` | Dropped. All tools are sent regardless of `defer_loading`; the downstream tool filter decides what finally reaches the upstream. |
| Tool-search server tool (`type` = `tool_search` or starting `tool_search_tool_`) | Silently removed from `tools` (D13). |
| Unsupported content blocks in `system` or `messages` (`server_tool_use`, server-tool result blocks, any unknown type) | Replaced by one placeholder text part (section 7.2); debug log (D13). A `document` follows `documentMode`: placeholder by default, `image_url` in `image` mode (section 7.2). |

---------------------------------------------------------------------------------------------------

## 5. Primitive rules

- Text MUST NOT be trimmed or re-flowed (D12). A text block whose text is empty or consists only of Unicode white-space characters is omitted entirely; any other text is kept byte for byte.
- When consecutive text parts are merged (sections 6, 7), they MUST be joined with exactly one `\n`.
- Numbers (in `tool_use.input`, tool schemas, `temperature`, `top_p`) MUST keep their textual form from the input; no double round-trip (`1.0` stays `1.0`, `12345678901234567890` stays as is, `1e2` stays `1e2`).
- `tool_use.input` is serialised as compact JSON (no insignificant whitespace), preserving key order and number text; non-ASCII characters MUST NOT be escaped, and `<`, `>`, `&`, `'` SHOULD NOT be escaped. Tool `parameters` are copied as parsed, same rules.
- Absent values MUST be omitted; `null` MUST NOT be emitted for any field defined here.
- A JSON value is a "string" / "number" / "boolean" / "object" / "list" only if it is exactly that JSON kind; arrays are not objects.

---------------------------------------------------------------------------------------------------

## 6. System

Input: string, list of blocks, absent or null.

1. String: used as is (no trim). Empty or white-space-only -> no system message.
2. List: apply the attribution rule (D18, below), then take the `text` of each remaining block whose `type` is `text`; every other block kind contributes the placeholder line of section 7.2 (debug log). Omit empty / white-space-only texts. Join the rest with one `\n`. Empty result -> no system message.
3. Output: exactly one `{"role":"system","content":"<string>"}` as the first message. Never an array of parts. `cache_control` and `citations` are ignored.

Attribution rule (D18): when `system` is a list, its FIRST element MUST be dropped if it is a text block whose text starts with `x-anthropic-billing-header:` (ASCII case-insensitive, compared from the first character; no leading-whitespace tolerance). Only the first element is examined; a string-form `system` and later elements are never touched. The marker varies per request and would defeat upstream prefix caching. Recognition context and per-client detail: see `04-harness-and-errors.md`.

---------------------------------------------------------------------------------------------------

## 7. Messages

### 7.0 Mid-turn system messages

Claude Code inserts `role: "system"` messages inside `messages` (for example a token-count note after a tool result). They MUST be mapped, never rejected.

- `midturnSystem: system` (default): each such message becomes one Chat `system` message at the same position. A string `content` is used verbatim; a list `content` yields text parts in order, every non-text block becoming the unsupported-block placeholder (7.2); an empty result drops the message.
- `midturnSystem: merge_into_user`: the text (`\n`-joined, placeholders included) becomes the leading text part of the next user message; if no user message follows, it becomes its own user message. The client-facing history order is unchanged.

### 7.1 Walk

Messages are processed in order. For each message the mapper builds a list of neutral items (below). A message that yields no items (for example one containing only white-space text) is removed; neighbours are not merged. A message containing an unsupported block always yields at least the placeholder item (7.2). The converted Chat sequence preserves message order; adjacent messages of the same role MUST NOT be merged except as specified in 7.4 (D14).

### 7.2 Block handling

| Block `type` | In a user message | In an assistant message |
|---|---|---|
| `text` (only blocks whose `type` is `text` count as text) | text item (section 5) | text item |
| `image` | image item (7.6) | dropped (D15) |
| `tool_result` | tool-result item (7.5) | placeholder |
| `tool_use` | placeholder | tool-use item (7.3) |
| `thinking`, `redacted_thinking` | dropped (D7) | dropped by default; profile-dependent re-injection (section 13); `redacted_thinking` is always dropped; a non-empty `signature` is ignored |
| every other `type` - including `server_tool_use`, `mcp_tool_use`, `web_search_tool_result` and every other `*_tool_result` server block, `search_result`, `container_upload`, `compaction`, and any unknown type (`document` is handled by the paragraph below) | placeholder | placeholder |

A `document` block follows `documentMode`: with `placeholder` (default) it is an unsupported block; with `image` it becomes an image part (7.6) when `source.type` is `base64` (media type `application/pdf` or `image/*`) or `url` with a non-empty `url`, and an unsupported block otherwise.

Risk: many Chat upstreams accept only `image/*` media types and reject a PDF data URL; `image` mode is therefore only for a provider known to accept PDFs, and the default stays `placeholder`.

Unsupported-block placeholder (D13). A block marked "placeholder" MUST be replaced, at the position where it stood, by one text item whose text is exactly `[omitted: unsupported content block '<type>']`, where `<type>` is the block's `type` string verbatim. The placeholder is an ordinary text item: it merges with adjacent text under the `
` rule of section 5, forms part of the assistant `content`, and is never dropped as empty. A message whose only content was an unsupported block therefore keeps the placeholder and is not removed. Each replacement MUST be logged at debug level (block type, message index, block index). The same replacement applies to the nested blocks of a tool_result (7.5).

### 7.3 Assistant messages

1. Text = all text items joined with `\n` (one `content` string regardless of interleaving with tool_use blocks).
2. `tool_calls` = one entry per tool_use block in block order: `{"id":<mapped id>,"type":"function","function":{"name":<mapped name, 8.3>,"arguments":<compact JSON text of input; "{}" when input is absent or null>}}`. Id mapping (D5) is a per-request map filled in message order: an id matching `^[a-zA-Z0-9_-]{1,64}$` that no earlier block claimed maps to itself; any other id (illegal characters, too long, empty) maps to `toolu_` plus the first 24 lower-case hex digits of SHA-256 over the id's UTF-8 bytes; a repeat of an already-claimed original appends `_2`, `_3`, ... (truncating to 64). The map is deterministic for a given body, so `tool_result` matching (7.4) always finds the same target. Input is never silently replaced when it has the wrong kind (R20).
3. The message is omitted if it has no text, no tool_calls and no re-injected reasoning (so a reasoning-only turn disappears under profile `none`, D7).
4. `content` = the text when non-empty; `""` when the message has tool_calls or re-injected reasoning but no text. `tool_calls` is present only when non-empty.
5. Reasoning fields: only per section 13.

### 7.4 User messages and tool results (D14, D15)

Let P be the message immediately preceding this user message in the converted sequence (after empty-message removal); let C be the ids of the `tool_calls` of P in call order when P is an assistant message, else empty.

Items of the user message are partitioned in original order into text items, image items and tool-result items.

1. Matching: for each mapped id in C, in call order, take the first not-yet-consumed tool-result item in this user message whose `tool_use_id`, passed through the same map (7.3), equals it; emit exactly one tool message `{"role":"tool","tool_call_id":<mapped id>,"content":<conveyed text, 7.5>}` and mark that item consumed. The tool messages MUST be contiguous and MUST immediately follow P, in the assistant's call order (not in the order the client listed the results). An id in C with no result gets no tool message; the assistant `tool_calls` entry is kept as is.
2. Image hoisting (D15): for each consumed tool-result item that contains images, in call order, after the last tool message, emit one user message `{"role":"user","content":[{"type":"text","text":"Images returned by tool call <tool_use_id>:"}, <image_url parts in order>]}`.
3. Remaining items, in original order, form ONE sibling user message placed after the tool messages and hoisted messages:
   - text items: consecutive ones joined with `\n` into one text part;
   - image items: image parts;
   - every unconsumed tool-result item ("orphan": no matching call in C, or a duplicate id) is converted to plain user text, never a tool message: a text part `Tool result (tool_use_id: <id>):` + `\n` + conveyed text, followed by the orphan's image parts, in original position. When the conveyed text is empty the part is just the first line.
4. Content shape of the sibling message: exactly one part and it is text -> `content` is that string; otherwise a list of parts (a lone image is a list of one). No parts -> no sibling message. A user message that is only tool results therefore yields only tool messages (and hoisted messages, if any).

Risk: Claude Code never sends an orphan result in a well-formed turn, but after interrupt or compaction a result can lose its call; it then reaches the model as user text rather than failing.

### 7.5 Tool-result conveyed text

Inputs: `content` (string, list of blocks, or absent/null), `is_error`.

1. String content: used verbatim.
2. List content: elements are processed in order. `text` blocks (white-space-only omitted) and placeholders (7.2) for every unsupported nested block (for example `document`) form the text lines, kept in original order and joined with `\n`; `image` blocks are collected as images (7.6); `tool_reference` blocks (objects with a string `tool_name`) are collected as references.
3. References: when at least one exists, append to the text (separated by `\n` when the text is non-empty) the compact JSON list `[{"type":"tool_reference","tool_name":"<mapped name>"}, ...]`, names mapped per 8.3 (a name absent from the map is converted by the character rule only), de-duplicated after mapping, first occurrence order.
4. If the text is empty and the result has images, the text is `(tool returned an image; see the next message)`.
5. `is_error` = true: the conveyed text MUST be prefixed with `Error: `; when the text is empty before prefixing it becomes `Error: (no details)`.
6. Otherwise empty stays `""`.

### 7.6 Images

- `source.type` `base64`: part `{"type":"image_url","image_url":{"url":"data:<media_type lower-cased>;base64,<data>"}}`; the data is not modified or validated beyond non-emptiness.
- `source.type` `url`: part with the url verbatim.
- No `detail` key. Images in assistant turns are dropped (D15). Invalid images are rejected (R22).

Risk: a text-only upstream rejects any image part; Claude Code attaches screenshots and image files routinely. See Open items.

---------------------------------------------------------------------------------------------------

## 8. Tools

### 8.1 Classification (in `tools` order)

| Tool entry | Result |
|---|---|
| custom (no `type`, or `type` = `custom`) | kept, converted (8.2) |
| tool-search type (D13) | silently removed |
| any other typed tool | rejected (R11) |
| custom without usable name | rejected (R09) |

A custom tool whose original name equals that of an earlier kept tool is dropped (first wins; debug log). If no tool survives, `tools`, `tool_choice` and `parallel_tool_calls` MUST be omitted (rules in section 9).

### 8.2 Function object

`{"type":"function","function":{"name":<target name>,"parameters":<schema>,"description":<text>}}`

- `parameters` = `input_schema` copied verbatim (no sanitising); absent or null -> `{"type":"object","properties":{}}`.
- `description` = the string verbatim when non-empty; otherwise omitted.
- `strict` is emitted verbatim when the original tool carries a boolean `strict` (no other value is emitted). Order of tools is preserved.

### 8.3 Tool-name map (D6)

Chat function names MUST match `^[a-zA-Z0-9_-]{1,64}$`. The mapper builds a per-request bidirectional map from (a) the names of kept request tools, in order, then (b) the names of all valid tool_use blocks in history, in order of first appearance. It is derived from the request only (stateless).

Target name of an original name N:
1. S = N with every maximal run of characters outside `[A-Za-z0-9_-]` replaced by one `_`. Existing underscores are never stripped.
2. If length(S) <= 64 the candidate is S; otherwise the candidate is the first 55 characters of S + `_` + the first 8 lower-case hex digits of SHA-256 over the UTF-8 bytes of N.
3. Equal original names share one target. If the candidate is already assigned to a different original name, the candidate becomes the first 55 characters of the candidate + `_` + the first 8 hex digits of SHA-256(N); if that is also taken, append `_2`, `_3`, ... (shortening the stem so the total stays <= 64) until free.

The map is used for: function names, assistant tool_call names, `tool_choice` function name, tool_reference names. A name not in the map (e.g. a tool_reference to an unknown tool) is converted by steps 1-2 only. The response side (stream and non-stream) applies the reverse map; names unknown to it pass through unchanged (`02-response-mapping.md`, `03-stream-state-machine.md`).

Conformance vectors (first 8 hex of SHA-256 of the UTF-8 original): `my_tool_v2` -> `c7ba60b6`; `a_b` -> `648fa9b3`; `日本` -> `cf2abf0c`; `日本語` -> `77710aed`; `mcp__averyverylongserver__averyverylongtoolname_that_goes_past_the_limit_of_64` (78 chars) -> `3609e2a4`.

---------------------------------------------------------------------------------------------------

## 9. tool_choice

Normalisation (after tool classification):

| Input | Output |
|---|---|
| `{"type":"auto"}` or string `auto` | `"auto"` |
| `{"type":"none"}` or string `none` | `"none"` |
| `{"type":"any"}`, string `any`, string `required` | `"required"` |
| `{"type":"tool","name":N}` | `{"type":"function","function":{"name":<mapped N>}}` |
| absent or null | omitted |

Rules:
- Invalid inputs are rejected: unknown string / type (R12), missing name (R13), name not among surviving tools (R14; a tool removed as tool-search counts as absent), forcing choices with no surviving tool (R15).
- When no tool survives and the choice is `auto` or `none`, `tool_choice` MUST be omitted.
- `disable_parallel_tool_use` = true with at least one surviving tool and a choice other than `none` -> `parallel_tool_calls: false`. In every other case no `parallel_tool_calls` key is emitted.
- Integration requirement: the downstream tool filter MUST NOT remove the tool named by a `function` choice.

Risk: Claude Code does not send `disable_parallel_tool_use` or string-form `tool_choice` today; rules are defensive. Upstreams that reject `parallel_tool_calls` are a configuration concern (Open items).

---------------------------------------------------------------------------------------------------

## 10. Sampling, limits, stop, user

| Output key | Rule |
|---|---|
| `temperature`, `top_p` | Emitted when present as numbers; no clamping or range mapping. |
| output limit key (`max_tokens` or `max_completion_tokens` per option) | Value = client `max_tokens`; if < 16 it MUST be raised to 16 (D19: the client's startup probe sends 1); then, if `maxOutputTokens` is set and the value exceeds it, the value is the cap. The floor is applied before the cap. |
| `stop` | `stop_sequences` as a list of strings, order and content preserved; empty list -> omitted. No truncation. |
| `user` | By `userIdMode` (default `off`): `off` = never sent; `passthrough` = `metadata.user_id` verbatim when it is a non-empty string; `hash` = first 32 lower-case hex digits of SHA-256 over the UTF-8 bytes of that string. |
| `response_format` | Only from `output_config.format` and only when `sendResponseFormat` is `true`. `{"type":"json_schema","schema":S}` -> `{"type":"json_schema","json_schema":{"name":<N>,"schema":S'}}`, where `<N>` is `output_config.format.name` when it is a non-empty string, else `"response"`, and `S'` is `S` copied with `additionalProperties: false` added to every schema object that declares `"type": "object"` and lacks the key (never into `anyOf`/`allOf`/`oneOf` branches as such). A format of any other type, or a missing/non-object `schema`, emits nothing and is logged at debug. |
| anything else | `n`, `seed`, penalties, `logprobs`, `top_k` equivalents are never emitted. |

Risk: Claude Code's `metadata.user_id` embeds device and account identifiers (newer clients send a JSON string); `passthrough` forwards them to a third-party upstream, which is why the default is `off`. An upstream that needs a stable per-user key for cache affinity should get `hash`.

---------------------------------------------------------------------------------------------------

## 11. Streaming

- `stream` JSON `true` -> `"stream": true` and `"stream_options": {"include_usage": true}` (D8). Any other accepted value (`false`, absent, null) -> neither key.
- The mapper's context records the client's streaming flag (section 14).
- Content-type of the outbound body is `application/json`. All other outbound headers: see `04-harness-and-errors.md` (D20).

---------------------------------------------------------------------------------------------------

## 12. Reasoning controls (default profile `none`)

Canonical effort E of a request (computed once, used by every profile):

1. Explicit: `output_config.effort` when a string whose ASCII-lower-case form is `low`, `medium`, `high`, `xhigh` or `max`. Any other value is ignored.
2. Else, when `thinking.type` is `enabled` and `thinking.budget_tokens` is a number B: B < 8192 -> `low`; 8192 <= B < 24576 -> `medium`; B >= 24576 -> `high`.
3. Else none.

Thinking state T:
- `disabled` when `thinking.type` is `disabled` (wins over any effort).
- `enabled` when `thinking.type` is `enabled`, or when E is explicit (including when `thinking` is absent or `adaptive`).
- otherwise `off` (absent, `adaptive` without explicit effort, unknown type).

Gate: when `sendReasoningEffort` is `false` (the default) NO reasoning control is sent, under any profile: no `reasoning_effort`, no `thinking`; E and T are not even consulted. The rest of this section and section 13 apply only when it is `true`.

Profile `none` output (with the gate open): when T is `enabled` and E is defined, emit `reasoning_effort` = E with `xhigh` and `max` collapsed to `high`. Nothing else is emitted for thinking (`thinking` and `reasoning_split` are never emitted by profile `none`). When T is `disabled` or `off`, nothing is emitted.

Thinking and redacted_thinking blocks in history are dropped (D7, 7.2).

Risk: thinking is enabled by default for current Claude Code models, so with `sendReasoningEffort` true every request carries `reasoning_effort`; an upstream that rejects the parameter returns 400 on every turn. That is why the option is off by default and enabled per provider after verification.

---------------------------------------------------------------------------------------------------

## 13. Vendor compat profiles (optional, selected by `compatProfile`)

Profiles are optional behavior layered on the mapping above (D7). A profile MUST be selected only by the explicit option. The reply-side counterpart (reasoning text -> thinking blocks) is in `02-response-mapping.md` / `03-stream-state-machine.md`.

Common inputs: canonical effort E and thinking state T from section 12. The `thinking` and `reasoning_effort` keys in the table below are emitted only when `sendReasoningEffort` is `true`; `reasoning_split` (profile `minimax`) and the history re-injection column are not reasoning controls and apply regardless of that option. Re-injectable history reasoning = text of `thinking` blocks (not empty and not white-space only; never trimmed); several blocks of one assistant message are joined with `\n`; `redacted_thinking` and signatures are never re-injected.

| Profile | History handling on assistant messages | Request keys |
|---|---|---|
| `none` | no reasoning keys; reasoning-only turns removed | section 12 only |
| `deepseek` | `reasoning_content` = re-injectable text when present; a reasoning-only turn is kept with `content: ""` | T `disabled` -> `thinking: {"type":"disabled"}`, no effort. T `enabled` -> `thinking: {"type":"enabled"}` and, when E is defined, `reasoning_effort`: `low`/`medium`/`high` -> `high`; `xhigh`/`max` -> `max`. T `off` -> neither key. Never `reasoning_split`. |
| `minimax` | `reasoning_details` = one entry per thinking block `{"type":"reasoning.text","index":<n>,"text":<text>}`; reasoning-only turn kept with `content: ""`; no `reasoning_content` | `reasoning_split: true` always; no `thinking`, no `reasoning_effort`. |
| `mimo` | `reasoning_content` as for `deepseek`; reasoning-only turn kept with `content: ""` | T `disabled` -> `thinking: {"type":"disabled"}`; T `enabled` -> `thinking: {"type":"enabled"}`; no effort key; no `reasoning_split`. |
| `zhipu` | no reasoning re-injection; reasoning-only turns removed | T `disabled` -> `thinking: {"type":"disabled"}`; T `enabled` -> `thinking: {"type":"enabled"}` and, when E is defined, `reasoning_effort` = E unchanged (`low`, `medium`, `high`, `xhigh`, `max`); no `reasoning_split`. |

All other mapping rules are identical across profiles.

---------------------------------------------------------------------------------------------------

## 14. Request-derived context (handed to the response side)

The mapper MUST return, alongside the Chat body, a context object that the reply mapping consumes (`02`, `03`):

| Field | Content |
|---|---|
| `inboundModel` | the inbound model name supplied by the router (D2) |
| `stream` | the client's streaming flag |
| `toolNameMap` | ordered (original, target) pairs; identity pairs MAY be omitted |
| `requestTokenEstimate` | integer from the estimator defined in `04-harness-and-errors.md`, computed over the inbound Messages body |

The context is derived from the request alone; nothing is persisted between requests.

---------------------------------------------------------------------------------------------------

## 15. Transport notes

- Target path: the provider base URL (which already contains any version segment) + `/chat/completions`; the inbound query string (for example `beta=true`) is not forwarded.
- Credentials and every outbound header: see `04-harness-and-errors.md` (allowlist, D20; forwarder writes the credential as Bearer and strips `anthropic-version`, `anthropic-beta`, `x-api-key`, D17).
- Body-affecting items stay here: `content-type: application/json`, `stream_options` (section 11).
- `count_tokens` is not mapped here (see 04).

---------------------------------------------------------------------------------------------------

## 16. Implementation requirements

- Work on a JSON document model (JsonNode); no reflection-based serialization; NativeAOT-safe; no dynamic code generation.
- Numbers keep their textual form end to end; no double conversion of passthrough values.
- Output is UTF-8; non-ASCII is not escaped; invalid JSON input yields R01-class errors, never an unhandled exception.
- Key order and text bytes are deterministic (section 1).
- SHA-256 via the platform library; hex lower-case.
- Input of any size or shape yields either a body or one of section 3's errors; the mapper MUST NOT throw for client-controlled data.

---------------------------------------------------------------------------------------------------

## 17. Open items (need live-upstream measurement or a product decision)

1. Which target upstreams reject `reasoning_effort`, `user`, `parallel_tool_calls` or `stream_options`, to decide per provider whether `sendReasoningEffort` / `userIdMode` may be enabled; whether further suppression switches are needed.
2. Whether upstreams with `xhigh`/`max` effort support should receive them instead of collapsing to `high` in profile `none`.
3. Upstream tolerance for `content: ""` on tool-call assistant messages and for empty tool-message content; fallbacks (null content, placeholder text) would become provider options.
4. Image parts versus text-only upstreams: a text-only upstream rejects any `image_url` part, while Claude Code attaches screenshots and image files routinely; the observed upstream failure mode (error, silent ignore) per provider is unmeasured.
4a. A provider option that strips image parts for text-only upstreams (candidate name `imageInput` with values `pass` / `strip`; stripped parts would be replaced by one placeholder text part, and hoisted-image messages would be removed). Not defined yet; trigger: the first bridged provider that is text-only.
5. Whether strict-alternation chat templates need the hoisted image user message merged with the sibling user message.
6. Model comprehension of the `tool_reference` JSON line, and the context cost of sending all `defer_loading` tools before the downstream filter prunes them.
7. MiniMax `reasoning_details` replay fields (identifier / format), DeepSeek handling of `reasoning_content` on non-tool turns, Zhipu preserved-thinking expectations (profile `zhipu` currently re-injects nothing).
8. Effective limits for `user` length / charset, and for more than 4 stop sequences, per upstream.
9. Per-vendor output-limit caps and whether reasoning models need `max_completion_tokens`.
10. Whether `\n` joining of consecutive text blocks (instead of separate text parts) is accepted by every target chat template without changing answers.
11. Unsupported blocks become placeholder text. Decide per type whether a content-preserving conversion is worth it instead (for example `search_result` text, `compaction` summaries), and measure how upstream models react to the placeholder (answering as if the content had been read).
12. Sampling parameters (`temperature`) rejected by some reasoning upstreams while thinking is on.
13. Request body size limits for base64 images on the upstream side.

---------------------------------------------------------------------------------------------------

## 18. Edge-case matrix (TARGET results)

Conventions: base request `{"model":"m","max_tokens":100,"messages":[...]}` unless stated; "default" = profile `none` with default provider options (`sendReasoningEffort` false, `userIdMode` off, `maxTokensField` `max_tokens`, no cap); messages abbreviated `s:` system, `u:` user, `a:` assistant, `t(id):` tool. Expected values list only the fields that matter; every other body key follows sections 1-11. "400/Rnn" = HTTP 400, error type `invalid_request_error`, message as in section 3. Decision tags in brackets.

### 18.1 Validation and rejection
| ID | Input | Expected |
|---|---|---|
| V01 | body `[]` | 400/R01 `Request body must be a JSON object.` |
| V02 | no `model` | 400 `model: Field required` |
| V03 | `model: 5` | 400 `model: Input should be a valid string` |
| V04 | no `max_tokens` | 400 `max_tokens: Field required` |
| V05 | `max_tokens: 0` | 400 `max_tokens: Input should be a valid integer greater than or equal to 1` |
| V06 | `max_tokens: 10.5` | 400 same integer message |
| V07 | `stream: "true"` | 400 `stream: Input should be a valid boolean` |
| V08 | `temperature: "0.5"` | 400 `temperature: Input should be a valid number` |
| V09 | `stop_sequences: "END"` | 400 `stop_sequences: Input should be a valid list` |
| V10 | `stop_sequences: ["a", 1]` | 400 `stop_sequences.1: Input should be a valid string` |
| V11 | no `messages` | 400 `messages: Field required` |
| V12 | `messages: []` | 400 `messages: at least one message is required` |
| V13 | `messages: [{"role":"user","content":"  \n"}]` | 400 `messages: no message contains usable content` |
| V14 | message role `system`, content `"note"` (messages `u, s, u`) | `u`, `s: "note"`, `u`: the system message keeps its position per 7.0 (default `system` mode); never 400 (R17 admits `system`) |
| V15 | `content: null` | 400 `messages.0.content: Field required` |
| V16 | `content: ["hi"]` | 400 `messages.0.content.0: Input should be a valid dictionary` |
| V17 | block `{"text":"x"}` without type | 400 `messages.0.content.0.type: Field required` |
| V18 | `system: {"type":"text","text":"x"}` | 400 `system: Input should be a valid string or list` |
| V19 | `system: "S"`, `messages: []` | 400 `messages: at least one message is required` (system alone is not enough) |
| V20 | invalid `temperature` and invalid `messages` together | the `temperature` error (order of checks) |

### 18.2 Field policy and unsupported content (D13)
| ID | Input | Expected |
|---|---|---|
| P01 | user content `[{text "see"},{document base64 pdf}]` | `u: "see\n[omitted: unsupported content block 'document']"` (placeholder merged with adjacent text; debug log) [D13] |
| P02 | tool `{"type":"web_search_20250305","name":"web_search"}` at index 2 | 400 `tools.2.type: "web_search_20250305" server tools are not supported` |
| P03 | tools `computer_20250124`, `bash_20250124`, `text_editor_20250728`, `memory_20250818`, `code_execution_20250522`, `web_fetch_20250910` (each alone) | 400 with the matching `{type}` in the P02 pattern |
| P04 | assistant content `[{server_tool_use ...}]` only | assistant message kept: `content: "[omitted: unsupported content block 'server_tool_use']"` [D13] |
| P05 | user content `[{web_search_tool_result ...}]` only | `u: "[omitted: unsupported content block 'web_search_tool_result']"` (message not empty) [D13] |
| P06 | tool `{"type":"tool_search_tool_regex_20251119","name":"tool_search_tool_regex"}` plus a custom tool | tool-search tool silently absent; custom tool kept |
| P07 | `cache_control` on system block, message blocks and tools | absent everywhere |
| P08 | `top_k`, `service_tier`, `context_management`, `container`, `mcp_servers`, `betas`, unknown field | absent |
| P09 | `metadata: {"user_id":"u1"}`, default options | no `user` (`userIdMode` default `off`) |
| P10 | `userIdMode: passthrough` with `metadata: {"user_id":"u1"}` / `{}` / `{"user_id":""}` / `{"user_id":5}` | `user: "u1"` / none / none / none |
| P11 | `userIdMode: hash`, user_id `user_0f1e_account_9a8b_session_7c6d` | `user: "f1875a946a35b4b918e331e8cc32f68f"` |
| P12 | `userIdMode: off` (explicit) with a user_id | no `user` |
| P13 | text block with `citations` | text only |
| P14 | tool with `defer_loading: true`, `eager_input_streaming`, `strict: "yes"` (not boolean) | tool present; those keys absent; no `strict` (boolean `strict` is forwarded per section 8, I90) |
| P15 | header `anthropic-beta: x` | no effect on body |
| P16 | custom tool `{"type":"custom","name":"x","input_schema":{...}}` | accepted as custom |
| P17 | tool without name | 400 `tools.0.name: Field required` |
| P18 | tool `input_schema: "x"` | 400 `tools.0.input_schema: Input should be a valid dictionary` |
| P19 | tool `mcp_toolset` | 400 `tools.0.type: "mcp_toolset" server tools are not supported` |
| P20 | user content `[{text "see"},{search_result ...}]` | `u: "see\n[omitted: unsupported content block 'search_result']"` |
| P21 | user content `[{document ...}]` only | `u: "[omitted: unsupported content block 'document']"` (message kept, not empty) [D13] |
| P22 | user content `[{document},{document}]` | `u: "[omitted: unsupported content block 'document']\n[omitted: unsupported content block 'document']"` |
| P23 | user content `[{"type":"foo","x":1}]` (unknown type) | `u: "[omitted: unsupported content block 'foo']"` |
| P24 | user `[{text "a"},{document},{text "b"}]` | `u: "a\n[omitted: unsupported content block 'document']\nb"` |
| P25 | assistant `[{text "ok"},{mcp_tool_use ...}]` | `a: content "ok\n[omitted: unsupported content block 'mcp_tool_use']"` |
| P26 | assistant `[{tool_use A},{document}]` | `a: content "[omitted: unsupported content block 'document']", tool_calls [A]` |
| P27 | placeholder type containing a quote or non-ASCII, e.g. type `a'b` | text `[omitted: unsupported content block 'a'b']` (type verbatim, no escaping) |

| P90 | `cache_control` on a text block, `forwardCacheControl: true` | the Chat text part carries the same `cache_control` object |
| P91 | `output_config.format` JSON schema, `sendResponseFormat: true` | `response_format: {"type":"json_schema","json_schema":{"name":"response","schema":<schema with additionalProperties:false on object types>}}` |
| P92 | `output_config.format`, default options | no `response_format` key |
### 18.3 System (D18, D12)
| ID | Input | Expected |
|---|---|---|
| S01 | `system: "  Be brief.\n"` | `s: "  Be brief.\n"` (no trim) |
| S02 | `system: [marker, identity, instructions]`, marker text `x-anthropic-billing-header: cc_version=2.1.0.a1b; cc_entrypoint=cli;` | marker dropped; `s:` = identity + `\n` + instructions |
| S03 | marker only as the SECOND element | kept in the text |
| S04 | `system` string starting with the marker text | kept |
| S05 | list whose only element is the marker | no system message |
| S06 | marker with different case `X-Anthropic-Billing-Header:` first | dropped |
| S07 | first element ` x-anthropic-billing-header:...` (leading space) | kept |
| S08 | white-space-only text blocks and an `image` block in the list | white-space-only blocks contribute nothing; the image block contributes `[omitted: unsupported content block 'image']`; `s:` is that line |
| S09 | `cache_control` on blocks | ignored |

### 18.4 Messages and text (D12, D14)
| ID | Input | Expected |
|---|---|---|
| M01 | user text blocks `"A\n"`, `"B"` | `u: "A\n\nB"` |
| M02 | user text `"  x"` | `u: "  x"` |
| M03 | user, whitespace-only user, user | whitespace-only message removed; two user messages remain, not merged |
| M04 | two consecutive user messages `"a"`, `"b"` | two user messages |
| M05 | assistant text blocks `"a"`, `"b"` | `a: content "a\nb"` |
| M06 | final assistant prefill `"{"` | last message `a: "{"` |
| M07 | assistant block `image` only | assistant message removed [D15] |
| M08 | thinking block inside a user message | dropped |
| M09 | `tool_result` inside an assistant message / `tool_use` inside a user message | placeholder text `[omitted: unsupported content block 'tool_result']` / `'tool_use'` at that position |
| M10 | identical input twice | byte-identical output |

| M90 | user, `system` "note", user | `u`, `s: "note"`, `u`: the system message keeps its position |
| M91 | same as M90 with `midturnSystem: merge_into_user` | `u: "note\n<text>"`, `u` (two user messages, no system) |
| M92 | `system` message whose content is `[image]` | `s: "[omitted: unsupported content block 'image']"` |
### 18.5 Images (D15)
| ID | Input | Expected |
|---|---|---|
| I01 | user `[text "look", image base64 png "AAAA"]` | `u: content [{text "look"},{image_url "data:image/png;base64,AAAA"}]` |
| I02 | user image only | content list with one image_url part |
| I03 | url source | url verbatim |
| I04 | `media_type: "IMAGE/PNG"` | `data:image/png;base64,...` |
| I05 | source type `file` | 400 `messages.0.content.0.source.type: Input should be 'base64' or 'url'` |
| I06 | base64 `media_type: application/pdf` | 400 `...source.media_type: Input should be an image media type` |
| I07 | base64 without `data` | 400 `...source.data: Field required` |
| I08 | `[text A, image, text B]` | parts: text A, image_url, text B |

| N90 | `document` base64 PDF, `documentMode: image` | `image_url` data URL part in the same user message |
| N91 | `document` base64 PDF, default options | placeholder text part `[omitted: unsupported content block 'document']` |
### 18.6 tool_use / tool_result (D4, D5, D14, D15)
| ID | Input | Expected |
|---|---|---|
| T01 | a `[text "ok", tool_use{id t1,name Read,input {"a":1}}]` then u `[tool_result t1 "done"]` | `a: content "ok", tool_calls [{id t1,type function,function {name Read, arguments "{\"a\":1}"}}]`; `t(t1): "done"` |
| T02 | a calls A then B; user results listed B then A | tool messages A then B (call order) [D14] |
| T03 | user `[tool_result t1, text "reminder"]` | `t(t1)` then `u: "reminder"` |
| T04 | user `[text "reminder", tool_result t1]` | `t(t1)` first, then `u: "reminder"` |
| T05 | orphan: previous message is a user message; user `[tool_result X "done"]` | no tool message; `u: "Tool result (tool_use_id: X):\ndone"` [D14] |
| T06 | a calls A; user `[tool_result A "x", tool_result Z "y"]` | `t(A): "x"`; `u: "Tool result (tool_use_id: Z):\ny"` |
| T07 | a calls A,B; user has only result A | `t(A)` only; `a` keeps both calls [D14] |
| T08 | duplicate results for A | first -> `t(A)`; second -> orphan user text |
| T09 | string content `"  line\n"` | `t: "  line\n"` verbatim |
| T10 | array content text `"a"`,`"b"` | `t: "a\nb"` |
| T11 | `is_error: true`, content `"File not found"` | `t: "Error: File not found"` [D13] |
| T12 | `is_error: true`, no content | `t: "Error: (no details)"` |
| T13 | no content | `t: ""` |
| T14 | content `[{"type":"text","text":""}]` | `t: ""` |
| T15 | result content `[text "Image file read.", image png]` for call C1 | `t(C1): "Image file read."`; then `u: [text "Images returned by tool call C1:", image_url]` [D15] |
| T16 | result content only an image | `t(C1): "(tool returned an image; see the next message)"`; hoisted user message follows |
| T17 | two results with images (C1, C2), results listed C2 first | tool messages C1, C2; then hoisted C1 message, then hoisted C2 message |
| T18 | results with images plus sibling text | order: tool messages, hoisted messages, sibling `u:` |
| T19 | tool_result content `[text "x", document]` | `t: "x\n[omitted: unsupported content block 'document']"` [D13] |
| T20 | tool_result content only `tool_reference` for `my.tool` (no such tool in tools) | `t: "[{\"type\":\"tool_reference\",\"tool_name\":\"my_tool\"}]"` |
| T21 | tool_result `[text "x", tool_reference "mcp__a__b"]` | `t: "x\n[{\"type\":\"tool_reference\",\"tool_name\":\"mcp__a__b\"}]"` |
| T22 | tool_result without `tool_use_id` | 400 `messages.2.content.0.tool_use_id: Field required` |
| T23 | tool_use without id / without name | 400 `...id: Field required` / `...name: Field required` |
| T24 | tool_use `input` absent / null | `arguments: "{}"` |
| T25 | tool_use `input: "{\"x\":1}"` (string) | 400 `messages.1.content.0.input: Input should be a valid dictionary` [D4: never silently substituted] |
| T26 | input `{"a":1.0,"b":12345678901234567890,"c":1e2}` | `arguments: "{\"a\":1.0,\"b\":12345678901234567890,\"c\":1e2}"` [D4] |
| T27 | input `{"b":1,"a":"<é>"}` | `arguments: "{\"b\":1,\"a\":\"<é>\"}"` (key order kept; no escaping) |
| T28 | tool_use id `call.1:abc` | `tool_calls[0].id` and `t.tool_call_id` both `toolu_<24 hex of SHA-256>`, stable on replay [D5] |
| T29 | assistant with only tool_use | `content: ""`, `tool_calls` present |
| T30 | tool message keys | exactly `role`, `tool_call_id`, `content` |
| T31 | assistant message that already carries tool_calls is followed by a user turn without any tool_result, with text | `u: text` only |
| T32 | tool_result content `[document]` only | `t: "[omitted: unsupported content block 'document']"` |

| T90 | assistant tool_use id `call.1:abc`, next user tool_result `tool_use_id: call.1:abc` | both mapped to the same `toolu_<24 hex>`; tool message matches |
| T91 | two tool_use blocks sharing id `dup` | ids `dup` and `dup_2`; a later result for `dup` matches the first |
### 18.7 Tool names and tools (D6)
| ID | Input | Expected |
|---|---|---|
| N01 | tool name `my tool.v2` | `my_tool_v2` |
| N02 | tools `a  b`, then `a...b` | `a_b`, then (collision) `a_b_eaf7617e` |
| N03 | `__x__`, `mcp__srv__tool` | unchanged |
| N04 | `日本語` | `_` |
| N05 | tools `日本語` then `日本` | `_` and `__cf2abf0c` |
| N06 | tools `a.b` then `a_b` | `a_b`, `a_b_648fa9b3` |
| N07 | 78-char legal name (vector in 8.3) | first 55 chars + `_3609e2a4` (64 chars) |
| N08 | history tool_use name `unlisted tool!` not in tools | map gains it: `unlisted_tool_` |
| N09 | tool_use name equal to a request tool name | same target as the tool |
| N10 | two tool definitions named `Read` | first kept, second dropped |
| N11 | tools `my tool.v2`, then `my_tool_v2` | `my_tool_v2`, `my_tool_v2_c7ba60b6` |
| N12 | context for N11 | `toolNameMap` contains (`my tool.v2`,`my_tool_v2`) and (`my_tool_v2`,`my_tool_v2_c7ba60b6`) |
| L01 | tool with description `d` and schema | `{type function, function {name, parameters <verbatim>, description "d"}}` |
| L02 | description absent or `""` | no `description` |
| L03 | `input_schema` absent | `{"type":"object","properties":{}}` |
| L04 | schema with `$schema`, `additionalProperties:false`, number `1.0` | copied verbatim (number text kept) |
| L05 | tools order `B`, `A` | same order |
| L06 | `tools: []` | no `tools` key |
| L07 | only a tool-search tool | no `tools`, no `tool_choice` |

| I90 | tool with `strict: true` | function object carries `"strict": true` |
| I91 | tool with `strict: "yes"` (not boolean) | no `strict` key |
### 18.8 tool_choice
| ID | Input | Expected |
|---|---|---|
| C01 | `{"type":"auto"}` | `"auto"` |
| C02 | `{"type":"any"}` | `"required"` |
| C03 | `{"type":"none"}` | `"none"` |
| C04 | `{"type":"tool","name":"my tool.v2"}` with that tool | `{"type":"function","function":{"name":"my_tool_v2"}}` |
| C05 | `{"type":"auto","disable_parallel_tool_use":true}` with tools | `"auto"` and `parallel_tool_calls: false` |
| C06 | `{"type":"auto","disable_parallel_tool_use":false}` | `"auto"`, no `parallel_tool_calls` |
| C07 | string `any` | `"required"` |
| C08 | string `required` / `auto` / `none` | `"required"` / `"auto"` / `"none"` |
| C09 | string `foo` | 400 `tool_choice: Input should be 'auto', 'any', 'tool' or 'none'` |
| C10 | `{"type":"weird"}` | 400 `tool_choice.type: Input should be 'auto', 'any', 'tool' or 'none'` |
| C11 | `{"type":"tool"}` | 400 `tool_choice.name: Field required` |
| C12 | `{"type":"tool","name":"nope"}` | 400 `tool_choice.name: no tool named "nope" in tools` |
| C13 | `{"type":"tool","name":"tool_search_tool_regex"}` where that tool was removed and another custom tool exists | 400 `tool_choice.name: no tool named "tool_search_tool_regex" in tools` |
| C14 | `{"type":"any"}` with no tools | 400 `tool_choice: "any" requires at least one tool` |
| C15 | `{"type":"auto"}` with no tools | no `tool_choice` |
| C16 | `{"type":"none"}` with tools and `disable_parallel_tool_use: true` | `"none"`, no `parallel_tool_calls` |
| C17 | `tool_choice: 5` | 400 `tool_choice: Input should be a valid dictionary` |

### 18.9 Limits, sampling, streaming (D8, D19)
| ID | Input | Expected |
|---|---|---|
| Q01 | `max_tokens: 4096` | `max_tokens: 4096` |
| Q02 | `max_tokens: 1` | `max_tokens: 16` [D19] |
| Q03 | `max_tokens: 15` / `16` / `17` | `16` / `16` / `17` |
| Q04 | `maxOutputTokens: 8192`, `max_tokens: 32000` | `8192` |
| Q05 | `maxTokensField: max_completion_tokens`, `max_tokens: 1000` | `max_completion_tokens: 1000`, no `max_tokens` |
| Q06 | `temperature: 0.7`, `top_p: 0.9` | emitted verbatim |
| Q07 | `stop_sequences: ["a","b"]` | `stop: ["a","b"]` |
| Q08 | `stop_sequences: []` | no `stop` |
| X01 | `stream: true` | `stream: true`, `stream_options: {"include_usage":true}` [D8] |
| X02 | `stream: false` or absent | neither key |
| X03 | context | `{inboundModel: <router's inbound name>, stream: <flag>, toolNameMap, requestTokenEstimate}` [D2] |
| X04 | body `model: "gpt-upstream"`, inbound name `claude-sonnet-4-5` | body model `gpt-upstream`; context `inboundModel: claude-sonnet-4-5` |

### 18.10 Reasoning and profiles (D7)
| ID | Input | Expected |
|---|---|---|
| K00 | `thinking {enabled, budget 4000}`, `output_config.effort high`, default options (`sendReasoningEffort` false) | no `reasoning_effort`, no `thinking`; history thinking still dropped |
| K01 | `sendReasoningEffort: true`, `thinking {enabled, budget 4000}` (profile none) | `reasoning_effort: "low"` |
| K02 | `sendReasoningEffort: true`; budgets 8191 / 8192 / 10000 / 24575 / 24576 / 31999 | `low` / `medium` / `medium` / `medium` / `high` / `high` |
| K03 | `sendReasoningEffort: true`; `output_config.effort: "medium"` with budget 31999 | `"medium"` (explicit wins) |
| K04 | `sendReasoningEffort: true`; effort `max` / `xhigh` | `"high"` (profile none) |
| K05 | `sendReasoningEffort: true`; `thinking {disabled}` with effort high | no `reasoning_effort` |
| K06 | `sendReasoningEffort: true`; `thinking {adaptive}` without effort / with effort `high` | nothing / `"high"` |
| K07 | `sendReasoningEffort: true`, no thinking, no effort | nothing emitted |
| K08 | `sendReasoningEffort: true`, `thinking {enabled}` without budget and without effort | nothing emitted (E undefined) |
| K09 | `sendReasoningEffort: true`; unknown effort `ultra` with budget 10000 | explicit ignored; `"medium"` from budget |
| K10 | history `[thinking "plan" sig "S", text "hi"]` (none) | `a: content "hi"` only |
| K11 | history `[thinking "plan"]` only (none) | assistant message removed; neighbours not merged |
| K12 | `redacted_thinking` + text | text only (all profiles) |
| K13 | deepseek + `sendReasoningEffort: true`, budget 4096, effort `medium`, history thinking "t" + tool_use | `thinking {"type":"enabled"}`, `reasoning_effort "high"`; assistant has `reasoning_content "t"` |
| K14 | deepseek + `sendReasoningEffort: true`, effort `max` | `reasoning_effort "max"` |
| K15 | deepseek + `sendReasoningEffort: true`, `thinking {disabled}` | `thinking {"type":"disabled"}`, no effort |
| K16 | deepseek + `sendReasoningEffort: true`, `thinking {adaptive}` no effort | no thinking keys |
| K17 | deepseek, reasoning-only history turn | assistant kept: `content ""`, `reasoning_content` |
| K18 | minimax + `sendReasoningEffort: true`, history thinking "t" | `reasoning_split: true`; assistant `reasoning_details [{"type":"reasoning.text","index":0,"text":"t"}]`; no `thinking`, no effort |
| K19 | mimo + `sendReasoningEffort: true`, thinking enabled | `thinking {"type":"enabled"}`; assistant `reasoning_content`; no effort |
| K20 | zhipu + `sendReasoningEffort: true`, effort `high` | `thinking {"type":"enabled"}`, `reasoning_effort "high"`; history reasoning absent; reasoning-only turn removed |
| K21 | upstream host `api.deepseek.com` with `compatProfile: none` | no vendor keys (explicit option only) |
| K22 | non-empty `signature` on a thinking block | never forwarded in any profile |
| K23 | `compatProfile: "foo"` | startup configuration error |
| K24 | deepseek with default `sendReasoningEffort` (false), thinking enabled budget 4096, effort `medium`, history thinking "t" + tool_use | no `thinking`, no `reasoning_effort`; assistant still has `reasoning_content "t"` (history re-injection is not a reasoning control) |
| K25 | minimax with default `sendReasoningEffort` | `reasoning_split: true` still sent; no `thinking`, no `reasoning_effort` |
| K26 | zhipu / mimo with default `sendReasoningEffort` | no `thinking`, no `reasoning_effort` |

### 18.11 Mixed realistic turns
| ID | Input | Expected |
|---|---|---|
| Z01 | Claude Code first turn: marker + 2 system blocks, 2 user text blocks, 3 tools (one tool-search), thinking 16000, stream | see `01-request-examples.md` Example 1 |
| Z02 | parallel calls, reversed result order, sibling reminder, thinking history | Example 2 |
| Z03 | image in tool_result with text and sibling | Example 3 |
| Z04 | deepseek profile turn (`sendReasoningEffort: true`) | Example 4 |
| Z05 | name collisions, forced tool, tool_reference, is_error | Example 5 |
| Z06 | orphan, unanswered call, reasoning-only turn removal | Example 6 |
| Z07 | unsupported blocks (document, server tool result) replaced by placeholders; typed server tool definition rejected | Example 8 |
