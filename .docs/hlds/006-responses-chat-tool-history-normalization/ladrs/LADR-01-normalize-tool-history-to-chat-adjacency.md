# LADR-01: Normalize Responses tool history to Chat adjacency

**Status:** Draft

## Context

OpenAI Responses represents prior tool activity as separate `function_call` and
`function_call_output` input items linked by `call_id`. Chat Completions represents the same exchange
as an assistant message with `tool_calls`, immediately followed by tool messages answering each
`tool_call_id`. Strict Chat upstreams reject a request when an assistant `tool_calls` message is not
followed by the required tool messages. A real review prompt against Moonshot/kimi failed with that
exact validation error for an orphaned `exec_command:0` tool call.

## Decision

**Normalize** prior-turn Responses tool history into Chat's assistant/tool adjacency model on the
OpenAI imposter `/responses`→Chat downgrade path.

When the downgrade sees a Responses `function_call` with a matching `function_call_output`, it preserves
the exchange as Chat history that satisfies the upstream invariant: assistant tool call first, matching
tool output immediately after. The match key is `call_id`; the tool name, arguments, call id, and output
content remain the client's original values.

This decision applies to prior-turn history carried in the request body. It does not change the active
`tools[]` catalog normalization from HLD 004 and does not alter response stream translation from HLD 004
LADR-05.

## Alternatives Considered

- **Pass history through as today** — rejected: strict Chat upstreams reject valid-looking HTTP requests
  before generation when tool-call adjacency is broken.
- **Drop all tool history on the downgrade path** — rejected: valid prior-turn tool exchanges are useful
  context and can be represented faithfully on the Chat wire.
- **Teach the response translator to repair this** — rejected: the upstream rejects the request before
  a response stream exists.

## Consequences

- Valid prior tool exchanges remain available to the model after downgrade.
- The request transformer must reason over related history items instead of converting each item in
  isolation.
- The scope of HLD 004 request normalization expands from active tool definitions to prior-turn tool
  history for the downgrade path.

## Related

- **HLD 004 LADR-02** — this refines request-only removal/conversion rules.
- **HLD 004 LADR-05** — response bridge remains separate and starts only after a successful upstream response.
