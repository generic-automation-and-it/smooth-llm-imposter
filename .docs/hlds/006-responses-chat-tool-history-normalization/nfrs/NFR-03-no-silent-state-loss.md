# NFR-03: No silent Responses state loss

**Status:** Draft

## Requirement

On the OpenAI imposter `/responses`→Chat downgrade path, 100% of request fields that reference
Responses-managed state or unsupported typed Items must be either faithfully converted, explicitly
removed by documented policy, or rejected with a dialect-shaped error. None may be silently dropped by
fallthrough conversion.

## Verification

- L0 transformer tests cover `previous_response_id`, `reasoning` Items, unsupported hosted-tool Items,
  and `text.format` Structured Outputs.
- L2 integration tests assert that rejected downgraded requests return OpenAI-shaped client errors
  before reaching the upstream.
- Code review checks that unknown Item types cannot fall through into default user messages.

## Acceptance Criteria

- A downgraded request containing `previous_response_id` is rejected, not forwarded with the field
  omitted.
- A downgraded request containing unknown or unsupported typed Items cannot produce empty user messages.
- A compatible `text.format` schema is converted to `response_format`; an unsupported format is rejected.

## Applies To

Goal 3 and Goal 4; the `/responses`→Chat request downgrade path.
