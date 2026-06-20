# NFR-01: Chat tool-history conformance

**Status:** Draft

## Requirement

For every Chat Completions request emitted by the OpenAI imposter `/responses`→Chat downgrade path,
100% of assistant messages containing `tool_calls` must be followed immediately by tool messages that
answer every referenced `tool_call_id` before any unrelated message appears.

## Verification

- L0 transformer tests inspect downgraded Chat `messages` and assert the adjacency invariant for paired,
  orphaned, and mixed tool histories.
- L2 host integration tests send a Responses request with prior-turn tool history through the real routing
  pipeline and assert the forwarded Chat body satisfies the same invariant.
- L3 strict-upstream eval reproduces the prior Moonshot/kimi failure and must receive a non-validation
  response when credentials are available.

## Acceptance Criteria

- No emitted Chat assistant `tool_calls` message has an unanswered `tool_call_id`.
- Orphaned Responses `function_call` and `function_call_output` items are absent from the forwarded Chat
  history.
- A request matching the observed Moonshot error shape no longer fails with the upstream
  "must be followed by tool messages" validation error.

## Applies To

Goal 1 and Goal 2; the prior-turn tool-history downgrade flow.
