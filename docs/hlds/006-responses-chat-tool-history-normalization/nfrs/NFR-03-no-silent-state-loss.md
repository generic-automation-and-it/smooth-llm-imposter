# NFR-03: No silent Responses state loss

**Status:** Accepted

## Requirement

On the OpenAI imposter `/responses`→Chat downgrade path, 100% of request fields that reference
Responses-managed state or unsupported typed Items must be either faithfully converted, explicitly
removed by documented policy, or rejected with a dialect-shaped error. None may be silently dropped by
fallthrough conversion.

## Verification

- L0 transformer tests cover `previous_response_id`, `conversation`, the `reasoning` request param,
  `reasoning` Items, unsupported hosted-tool Items, the passthrough generation knobs
  (`stop`/`metadata`/`logit_bias`/`logprobs`/`top_logprobs`), and `text.format` Structured Outputs.
- L2 integration tests assert that rejected downgraded requests return OpenAI-shaped client errors
  before reaching the upstream.
- Code review checks that unknown Item types cannot fall through into default user messages.
- L0 transformer tests cover message Items that convert to empty content (null content, empty
  `output_text`, unsupported-only content parts) and assert the message is dropped, while a non-empty
  message beside it survives.

## Acceptance Criteria

- A downgraded request containing `previous_response_id` or `conversation` is rejected, not forwarded
  with the field omitted; a real `/responses` route keeps both untouched.
- A downgraded request containing unknown or unsupported typed Items cannot produce empty user messages.
- A message Item that converts to empty content (null, empty, or only unsupported content parts) is
  dropped, not forwarded as a Chat message with neither content nor `tool_calls`; a non-empty message
  in the same request is unaffected.
- A compatible `text.format` schema is converted to `response_format`; an unsupported format is rejected.
- A `reasoning.effort` value is either converted to Chat `reasoning_effort` (compatible values) or
  dropped by documented policy (`none`/unknown); it is never silently forwarded in a shape the Chat
  upstream rejects.
- Chat-compatible generation knobs (`stop`/`metadata`/`logit_bias`/`logprobs`/`top_logprobs`) survive
  the downgrade rather than being dropped by allowlist fallthrough.

## Applies To

Goal 3 and Goal 4; the `/responses`→Chat request downgrade path.
