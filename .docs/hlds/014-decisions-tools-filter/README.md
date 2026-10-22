# Decision-Model Tool Pre-Filter — High-Level Design

| | |
|---|---|
| **Status** | Accepted — Linux AMD64 native and real upstream cache measurement pending |
| **Initiative** | Decisions |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | [GitHub #138](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/138) |
| **Last updated** | 2026-10-22 |

## Intent

Agent clients send large tool catalogs on every turn. An operator may opt into a decision-model pass that removes tools unlikely to be used before the LLM upstream sees them. The default path remains unchanged. The trade-off is serialized classifier latency and prompt/description egress to the configured decision service.

## Key Goals

### 1. Explicit global opt-in

The stage applies to imposter and passthrough routes, across OpenAI Chat, OpenAI Responses and Anthropic Messages. It is independent of per-provider caching, normalization and session forwarding. Configuration is startup-scoped; changing it requires a restart. See the [operator settings table](../../wiki/setup.md#optional-decision-model-tool-pre-filter).

**Acceptance criteria / DoD**

- Disabled is the default and no shipped appsettings or default container command enables it.
- Disabled requests retain the original body without parsing or classifier calls.
- Only effective upstream paths ending in Chat Completions, Responses or Messages are candidates; Messages count-tokens is excluded.

### 2. Conservative tool selection

Each positively understood, unprotected tool receives an independent use/skip question. The use probability is compared inclusively with the configured threshold. A missing or invalid answer keeps the tool. The catalog remains in its original order.

| Concern | OpenAI Chat | OpenAI Responses | Anthropic Messages |
|---|---|---|---|
| Catalog | Nested function/custom definitions | Flat function/custom; hosted tools; MCP servers; namespace wrappers | Custom and named server tools |
| Prompt | Message strings/text parts; system/developer last | Input string or message text; instructions last | Message strings/text blocks; system last |
| Ignore for prompt | Images and tool-role results | Reasoning, call outputs and hosted call items | Images, tool results and tool-use blocks |
| Protect history | Assistant function and custom tool calls | Function/custom/MCP calls, hosted call types, namespace and child references | Tool-use, server-tool-use and MCP-tool-use names |
| Choice cleanup | Named function/custom; nested allowed-tools list | Named function/custom; flat allowed-tools list | Named tool choice |
| Leave untouched | Deprecated functions/function-call fields | Unknown shapes | MCP server configuration |

Identity is catalog-index based; labels use name, nested function name, nested custom name, server label, then type. A namespace wrapper is one decision unit, protected by either its namespace name or a referenced child. Hosted call history protects matching catalog type stems; MCP server labels protect their server definition.

Unknown catalog or choice shapes are kept. History is never rewritten. Object-form choices naming removed tools are deleted; allowed-tools lists are pruned and deleted only when empty. String choices remain unchanged while tools survive. Removing every tool also removes tool choice.

**Acceptance criteria / DoD**

- One golden fixture per API exercises extraction, protection, decisions and exact output.
- Threshold equality and missing/invalid answers keep tools.
- A pass removing nothing returns the original string instance.
- Deprecated fields, unknown entries and protected history survive unchanged.

### 3. Bounded classifier work

State is tail-biased: newest user text first, then earlier turns newest-to-oldest; system/instructions fill remaining room. Textless tool-result turns are skipped. State is capped at 2,000 characters. Each question carries a label/description instruction capped at 120 characters, with independent criteria for use and skip.

At most 20 questions are sent per sequential chunk. The request carries model, state and questions; the response carries an answers object with per-question probabilities. The client rejects failed transport, non-success status and malformed envelopes; the filter owns conservative answer interpretation.

**Acceptance criteria / DoD**

- 45 unprotected tools produce chunks of 20, 20 and 5 using the same state.
- Stateful Responses deltas with non-null previous-response or conversation pointers are skipped.
- Empty body, absent/empty tools, path/body mismatch or missing prompt text cause no classifier request.

### 4. Preserve useful work on failure

Decision failure is fail-open, not inference failure. A failed chunk keeps its tools and stops further classification; earlier successful chunks remain applied. Caller cancellation is different: propagate it without a fail-open Warning or inference forward.

**Acceptance criteria / DoD**

- Transport, envelope, timeout and gate failures preserve unresolved tools.
- A second-chunk failure never invokes the third chunk.
- Who-probe replies invoke neither the classifier nor the inference upstream.
- Caller abort during classification makes no upstream call.

## Core Separation of Concerns

> Application owns every selection rule; Infrastructure owns decision-service transport; Host supplies the post-transform body at the existing forward seam.

## Guiding Principle

Remove only what the filter positively understands and can safely exclude; uncertainty keeps the caller's tool.

## Non-Goals

Per-provider overrides, decision caching, tool-count skip thresholds, prompt routing and cross-dialect translation are outside v1. Revisit caching or a threshold only when measured operator latency requires it.

## Diagrams

- [System Context](diagrams/c4-context.md)
- [Classification and forwarding sequence](diagrams/sequence-filter.md)

## Architecture Decisions

| LADR | Decision | Status |
|---|---|---|
| [01](ladrs/LADR-01-global-passthrough-opt-in.md) | Global opt-in; explicit passthrough-transparency exception | Accepted |
| [02](ladrs/LADR-02-fail-open-chunks.md) | Fail-open unresolved chunks; caller cancellation propagates | Accepted |
| [03](ladrs/LADR-03-independent-questions.md) | Per-tool probabilities and bounded sequential chunks | Accepted |
| [04](ladrs/LADR-04-singleton-gate.md) | Uncached singleton client and process-wide gate | Accepted |
| [05](ladrs/LADR-05-post-transform-seam.md) | Classify after who-probe/session translation, before forwarding | Accepted |
| [06](ladrs/LADR-06-history-and-choice-integrity.md) | Preserve history and clean dependent choices | Accepted |

## Non-Functional Requirements

| NFR | Attribute | Status |
|---|---|---|
| [01](nfrs/NFR-01-disabled-transparency.md) | Disabled/no-drop body identity | Accepted |
| [02](nfrs/NFR-02-classifier-data-egress.md) | Deliberate classifier egress; no sensitive classifier logging | Accepted |
| [03](nfrs/NFR-03-serialized-service-budget.md) | Serialized load and complete-response timeout | Accepted |
| [04](nfrs/NFR-04-native-runtime-parity.md) | Native/container classifier parity | Accepted |

## Verification evidence

- Solution build: no warnings/errors; deterministic suite: 773 tests pass.
- macOS ARM64 NativeAOT and Linux musl ARM64 container: all three APIs filter against live OpenJEV; disabled requests retain exact bytes.
- 60 tools: three 20-question chunks, 60→1 tools, 15.0 seconds on macOS and 14.9 seconds in the container. Two concurrent 60-tool requests: 31.0/29.8 seconds total; classifier concurrency stays one, all calls return 200, none return 504.
- Structured NativeAOT binding was exercised with non-default classifier address/path/model, threshold, two attempts and full-response timeout, on both native runtimes.
- Inference transport was a local capture stub. Real upstream cache-hit tokens were not measured; Linux AMD64 native-runner verification remains external. Status stays Accepted until these gates have evidence.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-22 | Implemented; L0/L2 and macOS/Linux ARM64 live checks pass; AMD64/cache gates pending | #138 |
