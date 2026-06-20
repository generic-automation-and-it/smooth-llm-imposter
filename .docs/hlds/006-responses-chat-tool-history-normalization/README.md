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
tool-call history: every assistant tool call is immediately followed by the corresponding tool result,
or the incomplete history is removed from the downgraded request.

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
- The response bridge stays independent; it translates upstream output shape, not request-history gaps.

---

## Diagrams

- [System Context (C1) + request-history flow](./diagrams/c4-context.md)

## Examples

- [Payload shape examples](./examples/README.md)

## Architecture Decisions (LADRs)

LADRs 01–N are strategic (*what* and *why*); later LADRs are tactical (*how*). Each is a
single decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-normalize-tool-history-to-chat-adjacency.md) | Normalize Responses prior-turn tool history into Chat's required assistant/tool adjacency model on the downgrade path. | Draft |
| [LADR-02](./ladrs/LADR-02-remove-incomplete-history-no-synthetic-tool-results.md) | Remove incomplete tool history; never synthesize tool results or remap tool identities. | Draft |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-chat-tool-history-conformance.md) | Upstream conformance | 100% of downgraded Chat requests satisfy tool-call adjacency invariants for emitted tool history. | Draft |
| [NFR-02](./nfrs/NFR-02-off-path-transparency.md) | Transparency | Zero request-history mutation outside matched OpenAI imposter `/responses`→Chat downgrade routes. | Draft |
