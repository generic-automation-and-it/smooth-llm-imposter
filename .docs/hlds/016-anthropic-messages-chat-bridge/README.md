# Anthropic Messages to Chat Completions Bridge — High-Level Design

| | |
|---|---|
| **Status** | In Discovery — spec complete, nothing implemented |
| **Initiative** | Claude Code on Chat-only upstreams |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | TBD — owner: maintainers; trigger: before acceptance |
| **Last updated** | 2026-10-24 |

## Intent

Claude Code speaks the Anthropic Messages API; many inexpensive or local upstreams serve only OpenAI Chat Completions. An operator may opt a provider into a bridge that translates the request to Chat and the reply (including streams) back to Messages, so the Claude harness works against those upstreams. Default behavior is unchanged. The trade-offs are lossy features (no hosted tools, no documents), unsigned thinking and approximate token counts.

> **Core Separation of Concerns:** routing decides *whether* a request is bridged; the bridge only translates wire shapes. It never picks models, filters tools or retries.

**Guiding principle:** faithful or loud — a translated turn is either correct or fails visibly with a Messages-shaped error. It is never silently wrong.

## Key Goals

### 1. Explicit per-provider opt-in

A provider option `AnthropicUpstreamApi` (`messages` default, `chat_completions`) turns the bridge on for matched imposter routes of that provider only ([LADR-01](ladrs/LADR-01-per-provider-opt-in.md)).

**Acceptance criteria / DoD**

- Providers without the option, passthrough routes and reserved Copilot routes are byte-identical to today ([NFR-01](nfrs/NFR-01-non-bridged-transparency.md)).
- Option plumbing covers every place a provider option lives (binding, env, cloner, both admin DTOs, validator) and is guarded by round-trip tests.
- A `chat_completions` provider that cannot produce a Bearer credential fails validation.

### 2. Faithful request mapping

Every Messages request the Claude Code harness sends maps to a Chat request a strict upstream accepts: system, text, images, tool use and results, tool choice, sampling, reasoning controls ([spec 01](spec/01-request-mapping.md)).

**Acceptance criteria / DoD**

- The request matrix passes with zero structural violations, and captured Claude Code bodies do too ([NFR-03](nfrs/NFR-03-request-fidelity.md)).
- Tool names and ids are normalized and reversed statelessly ([LADR-07](ladrs/LADR-07-tool-identity.md)).
- Representable-nowhere constructs follow one documented policy ([LADR-06](ladrs/LADR-06-allowlist-and-explicit-rejection.md)).

### 3. Faithful, incremental replies

Chat replies and streams become valid Messages replies and event streams, with correct block structure, stop reasons, reasoning and tool calls ([spec 02](spec/02-response-mapping.md), [spec 03](spec/03-stream-state-machine.md)).

**Acceptance criteria / DoD**

- Every matrix transcript and replay passes the Messages grammar checker ([NFR-04](nfrs/NFR-04-response-protocol-validity.md)).
- Streaming adds bounded latency and memory ([NFR-02](nfrs/NFR-02-stream-latency-and-memory.md)).
- A tool call's arguments are validated before they reach the client ([LADR-04](ladrs/LADR-04-validated-tool-arguments.md)).

### 4. Client-visible failure and accounting behavior

Errors, usage and token counting behave so the client's retry, overload, compaction and context accounting work ([spec 04](spec/04-harness-and-errors.md)).

**Acceptance criteria / DoD**

- Every failure reaches the client as a Messages error with a status-derived type ([LADR-05](ladrs/LADR-05-messages-shaped-errors-and-usage.md)).
- `count_tokens` is answered locally ([LADR-09](ladrs/LADR-09-local-count-tokens.md)); usage is never empty.
- Nothing the caller sent reaches a third-party Chat host except the translated body ([LADR-02](ladrs/LADR-02-seam-and-header-hygiene.md)).

## Architecture Decisions

| LADR | Decision | Status |
|---|---|---|
| [01](ladrs/LADR-01-per-provider-opt-in.md) | Per-provider opt-in on matched imposter routes | Draft |
| [02](ladrs/LADR-02-seam-and-header-hygiene.md) | Translate after planning, before the decision filter; header allowlist | Draft |
| [03](ladrs/LADR-03-incremental-streaming.md) | Incremental, bounded-state streaming translation | Draft |
| [04](ladrs/LADR-04-validated-tool-arguments.md) | Tool-call arguments held, validated, then emitted | Draft |
| [05](ladrs/LADR-05-messages-shaped-errors-and-usage.md) | Errors and usage always in Messages shape | Draft |
| [06](ladrs/LADR-06-allowlist-and-explicit-rejection.md) | Allowlist mapping; placeholders for history, rejection for intent | Draft |
| [07](ladrs/LADR-07-tool-identity.md) | Stateless tool id and name normalization, reversed on reply | Draft |
| [08](ladrs/LADR-08-reasoning-and-vendor-profiles.md) | Unsigned thinking; vendor profiles are explicit opt-ins | Draft |
| [09](ladrs/LADR-09-local-count-tokens.md) | `count_tokens` answered locally by a shared estimator | Draft |

## Non-Functional Requirements

| NFR | Attribute | Status |
|---|---|---|
| [01](nfrs/NFR-01-non-bridged-transparency.md) | Non-bridged traffic unchanged | Draft |
| [02](nfrs/NFR-02-stream-latency-and-memory.md) | Streaming latency and memory bounds | Draft |
| [03](nfrs/NFR-03-request-fidelity.md) | Mapped requests satisfy a strict Chat upstream | Draft |
| [04](nfrs/NFR-04-response-protocol-validity.md) | Every reply is a valid Messages reply | Draft |

## Specification

The normative behavior lives in `spec/`; each of 01 to 04 ends with an edge-case matrix whose rows are the conformance tests to write, and an Open items list. JSON wire examples are in `examples/`.

| File | Covers |
|---|---|
| [00 Cross-cutting decisions](spec/00-cross-cutting-decisions.md) | D1-D21, the rules every other spec file cites |
| [01 Request mapping](spec/01-request-mapping.md) | Pipeline, provider options, rejection list, fields, messages, tools, reasoning, vendor profiles |
| [02 Response mapping](spec/02-response-mapping.md) | Non-streaming reply: envelope, text, reasoning, tool use, stop reason, usage, errors |
| [03 Stream state machine](spec/03-stream-state-machine.md) | Chat SSE to Messages SSE: parser, frames, state machine, termination |
| [04 Harness and errors](spec/04-harness-and-errors.md) | Client endpoints, token estimator, error and header rules, probes |

## Known limitations

- Hosted tools (web search and other typed server tools) fail with HTTP 400; documents and server-tool blocks in history become a text placeholder, unless the operator opts a vision-capable provider into document-as-image mode.
- Thinking blocks carry an empty signature: they are display-only, and replaying a bridged session on a native Messages upstream that validates signatures can fail.
- Token counts are estimates; an upstream without vision rejects images.
- Long tool inputs reach the client when the call completes, not progressively.

## Open items

Interaction of the decision filter with bridged Claude Code bodies; image handling for text-only upstreams; which vendor profiles are needed first; real upstream cache-hit and latency measurement; Linux AMD64/ARM64 native verification; how the client renders empty or filtered replies. Details sit at the end of each spec file.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-24 | Discovery: HLD, nine LADRs, four NFRs and four normative spec files authored | — |
