# LADR-02: Remove incomplete history; do not synthesize tool results

**Status:** Accepted

## Context

An incomplete Responses history can contain a `function_call` without a matching
`function_call_output`, or a `function_call_output` without a corresponding call item. Chat Completions
has no faithful way to represent an assistant tool call that lacks its tool result. Inventing a result
would make the model see a tool outcome the client never provided.

## Decision

**Remove** incomplete tool-history items during the `/responses`→Chat downgrade instead of synthesizing
missing tool results or remapping tool identities.

An orphaned `function_call` is not emitted as a Chat assistant `tool_calls` message. An orphaned
`function_call_output` is not emitted as a Chat tool message. If a set of tool calls cannot be emitted
with complete adjacent outputs, the invalid subset is removed and the rest of the non-tool request
history continues through the existing downgrade rules.

Tool names and call ids are never renamed as part of this cleanup. Renaming would break the client's
tool-dispatch model and would require broader request/response correlation than this design permits.

## Alternatives Considered

- **Synthesize an empty tool response** — rejected: it creates false conversation state and can change the
  model's answer.
- **Convert orphaned calls into assistant text** — rejected: arguments are not assistant prose and this
  would reinterpret tool protocol data as natural language.
- **Reject the client request locally** — rejected for this compatibility layer: removing invalid history
  preserves the active user request where possible and follows HLD 004's removal-over-remap rule.

## Consequences

- Some prior-turn context can be lost when the client's history is incomplete.
- The upstream receives Chat-valid history without fabricated facts.
- Diagnostics and tests must make the removal explicit so it is not mistaken for accidental data loss.

## Related

- **LADR-01** — paired history is preserved; this decision covers the gaps.
- **HLD 004 LADR-02** — removal is preferred when faithful remap is impossible.
