# NFR-04: Message egress

**Status:** Draft

## Requirement

User text leaves the process only to the configured gate and optimizer endpoints, and only the candidate's human spans: no harness span, tool result, assistant turn, system prompt or non-text content is sent to either model. Each optimizer request carries only the optimization instructions and one message's human text — no tools, no conversation history, no other messages — and messages are sent one at a time, in series, never batched. No original or optimized message text is written to logs; logs carry the span hash, outcome (rewritten / declined / discarded / skipped), and original and forwarded sizes.

## Verification

L2 captures every request the stub gate and optimizer receive for a Claude Code–shaped turn containing reminders, hook output, a skill command block, an image and a tool result, and asserts only human spans appear. A log capture over the same run asserts no message text in any line. Repeated on the NativeAOT executable.

Implemented as an L2 file, `MessageOptimizerEgressTests`, that captures every request (headers and body) the stubbed gate and optimizer transports receive for a Claude Code-shaped Anthropic turn: a system prompt and tool definitions, two human messages around an assistant tool round, reminders, hook output, a skill command block with local-command output, an inline reminder, an image, a tool result and a caller credential header, each carrying a distinctive sentinel. It asserts that the gate state and the optimizer user message are exactly each candidate's human text and that no sentinel reaches either model; that every optimizer request has only `model`, `messages` (instructions plus one plain-text user message) and `stream`, and every gate request only `model`, `questions` and `state`; that the two candidates are four separate requests in the order gate, optimizer, gate, optimizer with at most one in flight; that, with the feature disabled, neither endpoint is called and the messages are forwarded unchanged; and that the withheld content is still forwarded to the upstream untouched. A native smoke on the macOS ARM64 NativeAOT executable repeats the same turn and matched on every point.

Two gaps against this NFR's wording are recorded rather than fixed (tests only here). First, the routing layer's Debug request dump (`Inbound <dialect> request ...`, enabled only by lowering the `SmoothLlmImposter` log level to Debug) writes the raw inbound body, so "no message text at any level" holds for the stage and the default level but not for that operator opt-in; the log test pins it as the single known exception so any new leaking line fails. Second, the stage logs outcome and original and forwarded sizes at Debug (`Message optimization: rewritten (a->b chars)`) but not the span hash the requirement names. Both need an owner decision on whether to change the code or the wording.

## Acceptance Criteria

- No harness, tool, assistant, system or non-text content in any gate or optimizer request.
- No message text in any log line at any level.
- Every optimizer request carries only system instructions and a single user message — no `tools`, `tool_choice` or history.
- Two candidates are sent as two sequential requests, never one.
- Disabled feature: zero requests to either endpoint.

## Applies To

Goals 2 and 3; LADR-01, LADR-03, LADR-05.
