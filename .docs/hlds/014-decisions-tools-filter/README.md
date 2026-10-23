# Decision-Model Tool Pre-Filter — High-Level Design

| | |
|---|---|
| **Status** | Accepted — Linux AMD64 native and real upstream cache measurement pending |
| **Initiative** | Decisions |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | [GitHub #138](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/138) |
| **Last updated** | 2026-10-23 |

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

Each positively understood, unprotected tool receives an independent use/skip question. The use probability is compared inclusively with the configured threshold (default 0.20 — drop only clearly irrelevant tools). A missing or invalid answer keeps the tool. A pass that would remove every tool keeps the catalog unchanged, since an all-skip verdict signals missing context rather than a tool-free task. A catalog using deferred loading — any `defer_loading: true` tool (including inside a namespace) or a `tool_search` tool — is skipped entirely: client-side deferral already keeps those tools out of the prompt, and removing the search gateway or Claude Code's "never call" placeholder would break it. The catalog remains in its original order; only `tools` and `tool_choice` are rewritten, and every other top-level value is forwarded byte-for-byte.

| Concern | OpenAI Chat | OpenAI Responses | Anthropic Messages |
|---|---|---|---|
| Catalog | Nested function/custom definitions | Flat function/custom; hosted tools; MCP servers; namespace wrappers | Custom and named server tools |
| Prompt | Latest user text, then earlier turns; system/developer only as fallback | Input string or message text; instructions only as fallback | Latest user text, then earlier turns; system only as fallback |
| Ignore for prompt | Images and tool-role results | Reasoning, call outputs and hosted call items | Images, tool-use blocks and any user turn carrying a tool result |
| Protect history | Assistant function and custom tool calls | Function/custom/MCP calls, hosted call types, namespace and child references | Tool-use, server-tool-use and MCP-tool-use names |
| Choice cleanup | Named function/custom; nested allowed-tools list | Named function/custom; flat allowed-tools list | Named tool choice |
| Leave untouched | Deprecated functions/function-call fields | Unknown shapes | MCP server configuration |

Identity is catalog-index based; labels use name, nested function name, nested custom name, server label, then type. A namespace wrapper is one decision unit, protected by either its namespace name or a referenced child. Hosted call history protects matching catalog type stems; MCP server labels protect their server definition.

Unknown catalog or choice shapes are kept. History is never rewritten. Understood object-form choices naming removed tools are deleted; allowed-tools lists are pruned and deleted only when empty. String choices remain unchanged while tools survive; because a pass never removes every tool, some tool always survives. An unrecognized forced hosted-tool choice can remain after its tool is removed, so the upstream may reject it; this is an accepted limit of conservative choice handling ([LADR-06](ladrs/LADR-06-history-and-choice-integrity.md)).

**Acceptance criteria / DoD**

- One golden fixture per API exercises extraction, protection, decisions and exact output.
- Threshold equality and missing/invalid answers keep tools.
- A pass in which every tool scores below the threshold returns the original string instance.
- Deferred-loading catalogs make no classifier request and return the original string instance.
- A pruned body keeps every top-level value other than `tools`/`tool_choice` byte-identical, including message escapes.
- A pass removing nothing returns the original string instance.
- Deprecated fields, unknown entries and protected history survive unchanged.

### 3. Bounded classifier work

When enabled, the classifier BaseUrl must be an absolute HTTP(S) URL; other URI schemes fail startup validation. Disabled classifier settings remain unvalidated.

State is labelled and request-first: `User request:` with the newest user text, then `Earlier conversation (newest first):` with prior `User:`/`Assistant:` turns. Assistant text after the request and system/instructions text are excluded — they change every agent step (Claude Code adds a `<total_tokens>` system message after each tool result) or bias the classifier toward tools they mention — and system text is used only as a `Context:` fallback when no conversation text exists. A user turn carrying a tool result is not a request. Closed spans of harness wrapper tags — Claude Code `<system-reminder>`, `<local-command-caveat>`, `<local-command-stdout>`/`<local-command-stderr>` and Conductor `<system_instruction>` — are removed and the text between them kept; a `<command-name>`/`<command-message>`/`<command-args>` block is removed only when `<local-command-stdout>`/`<local-command-stderr>` directly follows it (a local command such as `/model`, classified on the raw text before any stripping), never for a skill invocation, whose command and arguments are the request — even when Claude Code merges both into one user message; a block that starts and ends with the tags is dropped whole only when span pairing leaves an orphan closing tag (a reminder quoting the tag); an unclosed opener stays as ordinary text. The forwarded messages are never altered — reminder removal applies only to the classifier's copy. State is capped at 2,000 characters. Each question asks whether the assistant needs the named tool for the user's request, followed by its description, capped at 300 characters, with independent criteria for use and skip.

Tool answers are memoised in process memory per question (4,096 entries, oldest evicted first), keyed by a SHA-256 of model, state and that tool's question. A question's probability does not depend on the other questions in its chunk (measured |ΔP| ≤ 0.0002 on `kev-4b`), so only uncached tools are asked, in chunks. Later agent steps of the same user turn — including continuations whose history now protects a called tool — reuse the answers, keep an identical tool set (preserving the upstream prompt-cache prefix) and skip the classifier. Only valid answers are cached; a missing or invalid answer is asked again next time ([LADR-07](ladrs/LADR-07-chunk-memo-and-byte-preserving-output.md)).

At most 20 questions are sent per sequential chunk. The request carries model, state and questions; the response carries an answers object with per-question probabilities. The client rejects failed transport, non-success status and malformed envelopes; the filter owns conservative answer interpretation.

**Acceptance criteria / DoD**

- 45 unprotected tools produce chunks of 20, 20 and 5 using the same state.
- Stateful Responses deltas with non-null previous-response or conversation pointers are skipped.
- Empty body, absent/empty tools, path/body mismatch or missing prompt text cause no classifier request.
- A Claude Code–shaped turn sends `User request:` plus earlier turns as state: no reminder, Conductor or local-command wrappers, hook/system messages, tool-result turns or post-request assistant text.
- An identical second request makes no classifier call and yields the same output; a tool-call continuation of the same request asks nothing new; an invalid answer is re-asked alone.

### 4. Preserve useful work on failure

Decision failure is fail-open, not inference failure. A failed chunk keeps its tools and stops further classification; earlier successful chunks remain applied. Caller cancellation is different: propagate it without a fail-open Warning or inference forward.

**Acceptance criteria / DoD**

- Transport, envelope, timeout and gate failures preserve unresolved tools.
- A second-chunk failure never invokes the third chunk.
- Who-probe replies invoke neither the classifier nor the inference upstream.
- Caller abort during classification makes no upstream call.

## Core Separation of Concerns

> Application owns every selection rule; Infrastructure owns decision-service transport; Host supplies the post-transform body at the existing forward seam.

[HLD 015](../015-shared-decision-model-integration/README.md) proposes a consumer-neutral internal integration. It does not supersede this HLD's accepted runtime contract; tool eligibility, questions, batching, thresholds, history protection and fail-open outcomes stay here.

## Guiding Principle

Remove only what the filter positively understands and can safely exclude; uncertainty keeps the caller's tool.

## Non-Goals

Per-provider overrides, persistent or distributed decision caching, tool-count skip thresholds, prompt routing and cross-dialect translation are outside v1. Revisit caching or a threshold only when measured operator latency requires it.

## Diagrams

- [System Context](diagrams/c4-context.md)
- [Classification and forwarding sequence](diagrams/sequence-filter.md)

## Architecture Decisions

| LADR | Decision | Status |
|---|---|---|
| [01](ladrs/LADR-01-global-passthrough-opt-in.md) | Global opt-in; explicit passthrough-transparency exception | Accepted |
| [02](ladrs/LADR-02-fail-open-chunks.md) | Fail-open unresolved chunks; caller cancellation propagates | Accepted |
| [03](ladrs/LADR-03-independent-questions.md) | Per-tool probabilities and bounded sequential chunks | Accepted |
| [04](ladrs/LADR-04-singleton-gate.md) | Uncached singleton client and process-wide gate (memo lives in the filter, LADR-07) | Accepted |
| [05](ladrs/LADR-05-post-transform-seam.md) | Classify after who-probe/session translation, before forwarding | Accepted |
| [06](ladrs/LADR-06-history-and-choice-integrity.md) | Preserve history and clean dependent choices | Accepted |
| [07](ladrs/LADR-07-chunk-memo-and-byte-preserving-output.md) | In-memory per-question memo; byte-preserving output; deferred-loading skip | Accepted |

## Non-Functional Requirements

| NFR | Attribute | Status |
|---|---|---|
| [01](nfrs/NFR-01-disabled-transparency.md) | Disabled/no-drop body identity | Accepted |
| [02](nfrs/NFR-02-classifier-data-egress.md) | Deliberate classifier egress; no sensitive classifier logging | Accepted |
| [03](nfrs/NFR-03-serialized-service-budget.md) | Serialized load and complete-response timeout | Accepted |
| [04](nfrs/NFR-04-native-runtime-parity.md) | Native/container classifier parity | Accepted |

## Verification evidence

- Solution build: no warnings/errors; deterministic suite: 790 tests pass (L0/L1/L2), matching the root changelog.
- macOS ARM64 NativeAOT and Linux musl ARM64 container: all three APIs filter against live OpenJEV; disabled requests retain exact bytes.
- 60 tools: three 20-question chunks, 60→1 tools, 15.0 seconds on macOS and 14.9 seconds in the container. Two concurrent 60-tool requests: 31.0/29.8 seconds total; classifier concurrency stays one, all calls return 200, none return 504.
- Structured NativeAOT binding was exercised with non-default classifier address/path/model, threshold, two attempts and full-response timeout, on both native runtimes.
- Review follow-up: 444 Application tests pass, including passthrough fast-path and classifier URL regressions. macOS ARM64 NativeAOT startup rejects enabled file/FTP classifier URLs, accepts HTTP(S), and ignores unsupported schemes while disabled.
- 2026-10-23 field defect: a Claude Code session sent the head of its CLAUDE.md `<system-reminder>` as state on every chunk; `kev-4b` then scored every tool 0.22–0.49 and a 0.60 threshold stripped the catalog. Direct probes of 10 real tools (`kev-4b`): with the request as state and the current question wording, irrelevant tools score 0.04–0.09 and the requested `GetWorkspaceDiff`/`DiffComment` 0.86/0.83; `openjev` separates further (≤0.01 vs 0.98) at about 5× the latency. Core tools land 0.07–0.65 depending on the task, hence the 0.20 default.
- 2026-10-23 rework from captured Claude Code bodies (local capture server, no upstream): request-only labelled state beat request+system on all cases (`kev-4b`, 56 real tools, kept at 0.20 — review 25 vs 45, web 13 vs 39, "hi" 1 vs 52) at about half the latency (4.9 s vs 8.7 s); `noul` questions were uncalibrated (0.4–0.6 for everything); post-request assistant text changed the kept set every agent step; identical requests scored identically (max |ΔP| 0.0002). Live JIT Host run on the captured 54-tool body: 23/54 kept in 6.4 s, the repeat answered from the memo in 1 ms with the same set, and every non-`tools` top-level value forwarded byte-identical.
- Inference transport was a local capture stub. Real upstream cache-hit tokens were not measured; Linux AMD64 native-runner verification remains external. Status stays Accepted until these gates have evidence.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-22 | Implemented; L0/L2 and macOS/Linux ARM64 live checks pass; AMD64/cache gates pending | #138 |
| 2026-10-22 | Review follow-ups: transformer guard tests, HTTP(S) URLs, transparency/hosted-choice caveats; HLD 015 proposed shared boundary | #138 |
| 2026-10-23 | Labelled request-first state (no post-request assistant text, system only as fallback, reminder blocks and tool-result turns excluded); need-question wording; all-skip keeps catalog; deferred-loading skip; in-memory chunk memo; byte-preserving output; threshold 0.20; removed names logged (NFR-02) | #138 |
| 2026-10-23 | Review fixes: memo keyed per tool question so tool-call continuations reuse answers; only valid answers cached; text between reminder spans kept | #141 |
| 2026-10-23 | Conductor `<system_instruction>` and Claude Code local-command wrappers stripped from state; fail-open warning names HTTP status and configured model | — |
