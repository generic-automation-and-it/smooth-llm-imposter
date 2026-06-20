# Responses Chat Tool History Normalization — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Owner** | Routing feature (@generik0) |
| **Tracker** | [Issue #19 — Codex tool-name 400 on opencode/Moonshot](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/19) |
| **Last updated** | 2026-06-20 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution is tracked in the issue/work tracker.

## Intent

HLD 004 made OpenAI `/responses` requests usable against Chat Completions-only upstreams by
normalizing the active tool catalog, downgrading the outbound request, and translating the streamed
response back to Responses events. A later real Codex review prompt exposed a different strict-upstream
failure: prior-turn Responses history can contain a `function_call` item without its matching
`function_call_output`, and the current downgrade forwards that as a Chat assistant `tool_calls`
message that violates Chat Completions ordering rules.

This HLD specifies the missing request-history normalization for the same OpenAI imposter
`/responses`→Chat downgrade path. The target outcome is that strict Chat upstreams receive only valid
Chat-compatible history: paired tool calls survive, incomplete history is removed, Responses-only
state is not silently lost, and request fields whose shapes differ between Responses and Chat are
explicitly converted or rejected.

Source baseline: OpenAI's migration guide frames the API difference as endpoint, output-shape, and
state-management changes, with Responses using typed Items (`message`, `reasoning`, `function_call`,
`function_call_output`) rather than only Chat messages. It also calls out function-call shape
differences, `text.format` for Structured Outputs, typed streaming events, and `call_id` correctness as
common migration concerns. See [Migrate to the Responses API](https://developers.openai.com/api/docs/guides/migrate-to-responses?update-generation-endpoints=chat-completions).

## Key Goals

### 1. Preserve valid prior-turn tool exchanges on the Chat wire

When a Responses request includes paired `function_call` and `function_call_output` items, the
downgrade should represent that prior exchange in Chat Completions shape without changing the tool
identity, call id, arguments, or output text. The transform should maintain Chat's invariant that an
assistant tool-call message is followed by tool messages answering every `tool_call_id` in that
assistant message.

**Acceptance criteria / DoD**

- A Responses history pair with matching `call_id` is downgraded to adjacent Chat assistant/tool
  messages.
- Multiple tool calls from the same assistant turn remain valid: every emitted Chat `tool_call_id`
  has a matching following tool message before any unrelated message appears.
- Tool names and call ids are preserved; the proxy does not invent replacement identifiers.

### 2. Remove incomplete or invalid tool history instead of inventing data

If Responses history contains an orphaned `function_call`, an orphaned `function_call_output`, or an
ordering pattern that cannot be represented as a valid Chat Completions conversation, the downgrade
removes the invalid history item(s). It must not synthesize a fake tool output, fake assistant call,
or fallback content, because that would add model-visible conversation facts the client never sent.

**Acceptance criteria / DoD**

- Orphaned `function_call` items do not produce Chat assistant `tool_calls` messages.
- Orphaned `function_call_output` items do not produce Chat tool messages.
- The active user/developer/system text in the request remains intact after invalid history is removed.
- The transform is scoped only to the OpenAI imposter `/responses`→Chat downgrade path.

### 3. Keep the HLD 004 boundaries intact

This design refines HLD 004's request normalization and downgrade behavior; it does not introduce
new response rewriting, cross-dialect translation, persistence, or client-specific configuration. The
response-side LADR-05 bridge remains responsible only for Chat→Responses wire-shape translation after
a successful upstream response begins.

**Acceptance criteria / DoD**

- Passthrough/default routes, Anthropic routes, OpenAI `responses` upstreams, and direct
  `/chat/completions` callers stay byte-transparent for request history.
- No request-history state is carried into the response translator.
- HLD 004 context remains accurate: request compatibility is achieved by removal or structural
  conversion, not by tool-name remapping or response-side repair.

### 4. Classify every Responses-to-Chat downgrade gap

The downgrade must not rely on incidental behavior for Responses fields that Chat does not understand.
Every request field or input Item should have an explicit policy: preserve, convert, remove, or reject.
This is especially important for `previous_response_id`, `store`, `reasoning` Items, built-in hosted
tool Items, multimodal content, and Structured Outputs.

**Acceptance criteria / DoD**

- The design records a downgrade policy for each documented Chat/Responses difference that can appear
  in request history.
- Fields that cannot be represented statelessly on the Chat wire are not silently dropped.
- Structured Outputs are converted from Responses shape to Chat shape when possible.
- Typed Items with no faithful Chat representation are removed or rejected by policy, not accidentally
  converted into empty user messages.

## Core Separation of Concerns

> The proxy may make prior-turn history Chat-valid, but it must not create conversation facts.

Responses and Chat Completions encode tool history differently. The proxy owns the wire-shape
downgrade when it routes a Responses client to a Chat-only upstream, so it must also own the
conversation-shape invariants that Chat enforces. That ownership is bounded: it can preserve valid
history and remove invalid history, but it cannot invent missing tool outputs or reinterpret what a
tool did.

## Guiding Principle — Preserve pairs, drop gaps

> Valid tool-call pairs survive; gaps disappear.

- The downgrade preserves paired history where it can be represented faithfully on the Chat wire.
- The downgrade deliberately removes incomplete tool history rather than fabricating missing messages.
- The downgrade records explicit preserve/convert/remove/reject policies for Responses-only constructs.
- The response bridge stays independent; it translates upstream output shape, not request-history gaps.

---

## Diagrams

- [System Context (C1) + request-history flow](./diagrams/c4-context.md)

## Examples

- [Payload shape examples](./examples/README.md)

## OpenAI Migration Gap Analysis

The OpenAI migration guide is written for Chat→Responses migration. This router performs the inverse
on a scoped path: a Responses-mode client is routed to a Chat-only upstream. The same differences still
apply, but their handling is inverted.

| Difference from OpenAI guide | Current coverage | Additional transformation needed |
|---|---|---|
| Endpoint change: Chat uses `/v1/chat/completions`; Responses uses `/v1/responses`. | Covered by HLD 004 path override on matched OpenAI imposter `/responses` requests. | No additional transformation for HLD 006. |
| Simple messages are broadly compatible: Chat `messages[]` can become Responses `input`; inverse can become Chat `messages[]`. | Partially covered by existing `input`/`instructions` to `messages` conversion. | Keep, but validate typed `message` Items so unsupported content parts do not become empty Chat messages. |
| Responses separates system/developer guidance into top-level `instructions` or compatible Items. | Covered by converting `instructions` to a Chat `system` message and folding `developer` to `system` for strict Chat upstreams. | No additional transformation unless future upstreams support `developer`. |
| Responses uses typed Items; `message`, `reasoning`, `function_call`, and `function_call_output` are distinct. | Partially covered for `message`, `function_call`, and `function_call_output`; not covered for `reasoning` or other non-message output Items. | Add explicit Item policy. Preserve message Items, pair function Items, remove/reject unsupported Items such as reasoning and hosted-tool outputs. |
| Function definitions have different shapes: Chat nests under `function`; Responses keeps fields flat. | Covered by HLD 004 active tool conversion and normalization. | No extra HLD 006 work for active `tools[]`; prior-turn tool history still needs adjacency cleanup. |
| Function call outputs are correlated by `call_id`; sending a result without the matching id is a common migration error. | HLD 006 covers this for orphaned calls/outputs. | Implement pair validation and removal/rejection for incomplete history. A structured `function_call_output.output` array is JSON-stringified into the Chat `tool` message `content` (Chat tool content must be a string), preserving the structure as JSON text rather than reducing it to a text part. |
| Structured Outputs moved from Chat `response_format` to Responses `text.format`. | Not covered in the inverse direction; current downgrade copies `response_format` but does not map `text.format`. | Convert compatible `text.format` JSON schema requests to Chat `response_format`; remove or reject unsupported text formats. |
| Conversation state differs: Chat callers replay `messages`; Responses can replay `output`, use `previous_response_id`, or use stored conversations. | Manual replay via `input` is partially covered. `previous_response_id` cannot be resolved by a stateless proxy. | Reject `previous_response_id` on the downgrade path; clients must replay the needed Items in `input` for Chat-compatible routing. `store` is forwarded unconditionally — it is a valid Chat Completions parameter, not a Responses-only state pointer, so passthrough is safe and a per-provider capability gate is unwarranted (revisit only if a strict upstream is observed to 400 on it). |
| Responses includes native hosted tools that Chat Completions cannot use natively. | HLD 004 drops unsupported active tool definitions for strict Chat upstreams. | Hosted-tool prior-turn Items are removed only when their type carries the `_call`/`_call_output` suffix (`web_search_call`, `mcp_call`, …); hosted Items without that suffix (`mcp_list_tools`, `mcp_approval_request`) are rejected by policy (LADR-03 fail-fast), never converted into user messages. |
| Streaming output differs: Chat chunks vs typed Responses events. | Covered by HLD 004 LADR-05 response bridge. | No HLD 006 change; keep response translator independent from request-history cleanup. |

## Architecture Decisions (LADRs)

LADRs 01–N are strategic (*what* and *why*); later LADRs are tactical (*how*). Each is a
single decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-normalize-tool-history-to-chat-adjacency.md) | Normalize Responses prior-turn tool history into Chat's required assistant/tool adjacency model on the downgrade path. | Draft |
| [LADR-02](./ladrs/LADR-02-remove-incomplete-history-no-synthetic-tool-results.md) | Remove incomplete tool history; never synthesize tool results or remap tool identities. | Draft |
| [LADR-03](./ladrs/LADR-03-explicit-item-field-downgrade-policy.md) | Classify Responses-only Items and fields as preserve, convert, remove, or reject before downgrade. | Draft |
| [LADR-04](./ladrs/LADR-04-convert-compatible-structured-output-shape.md) | Convert compatible Responses `text.format` Structured Outputs to Chat `response_format`; reject unsupported formats. | Draft |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-chat-tool-history-conformance.md) | Upstream conformance | 100% of downgraded Chat requests satisfy tool-call adjacency invariants for emitted tool history. | Draft |
| [NFR-02](./nfrs/NFR-02-off-path-transparency.md) | Transparency | Zero request-history mutation outside matched OpenAI imposter `/responses`→Chat downgrade routes. | Draft |
| [NFR-03](./nfrs/NFR-03-no-silent-state-loss.md) | State correctness | Downgraded requests containing Responses state pointers are rejected or explicitly classified; none are silently dropped. | Draft |
