# NFR-04: Message egress

**Status:** Draft

## Requirement

User text leaves the process only to the configured gate and optimizer endpoints, and only the candidate's human spans: no harness span, tool result, assistant turn, system prompt or non-text content is sent to either model. Each optimizer request carries only the optimization instructions and one message's human text — no tools, no conversation history, no other messages — and messages are sent one at a time, in series, never batched. No original or optimized message text is written to logs; logs carry the span hash, outcome (rewritten / declined / discarded / skipped), and original and forwarded sizes.

## Verification

L2 captures every request the stub gate and optimizer receive for a Claude Code–shaped turn containing reminders, hook output, a skill command block, an image and a tool result, and asserts only human spans appear. A log capture over the same run asserts no message text in any line. Repeated on the NativeAOT executable.

## Acceptance Criteria

- No harness, tool, assistant, system or non-text content in any gate or optimizer request.
- No message text in any log line at any level.
- Every optimizer request has exactly two parts — instructions and one message — and no `tools`, `tool_choice` or history.
- Two candidates are sent as two sequential requests, never one.
- Disabled feature: zero requests to either endpoint.

## Applies To

Goals 2 and 3; LADR-01, LADR-03, LADR-05.
