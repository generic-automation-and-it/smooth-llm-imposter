# Shared Decision-Model Integration — High-Level Design

| | |
|---|---|
| **Status** | Accepted |
| **Initiative** | Decisions |
| **Owner** | SmoothLlmImposter maintainers |
| **Related tracker** | [GitHub #138](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/138) — first consumer; [#156](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/156) — reconcile and extraction |
| **Last updated** | 2026-10-24 |

## Intent

SmoothLlmImposter already calls OpenJEV to classify tools, but the otherwise reusable transport depends on tool-filter settings. Establish one internal decision-model integration whose service contract and operational policy do not depend on any consuming feature. Tool filtering remains the first consumer; this design adds no public endpoint or new product behavior.

## Key Goals

### 1. Separate evidence from feature policy

A consumer supplies state and independently keyed choice questions. The integration returns the service's answer envelope or a failure; it does not select tools, apply thresholds, traverse a decision tree, or authorize an action. Deterministic rules and model-assisted choices remain distinct: the consumer decides whether a model call is warranted and how its evidence affects behavior.

**Acceptance criteria / DoD**

- The shared contract has no tool, routing-path, FIX/SKIP or review-workflow concepts.
- Question keys and answer fields survive without consumer-specific rewriting.
- Missing answers and confidence/probability interpretation remain consumer responsibilities.
- A non-tool choice fixture can use the same integration without introducing a second production feature.

### 2. Share the operational boundary

Consumers use one configured OpenJEV destination and one process-wide concurrency budget. Address, path, default model, attempt count and timeout are integration concerns; opt-in, question composition, state limits, batching and action thresholds are consumer concerns. Enabling one consumer must not enable another.

Preserve the proven service contract: model, state and keyed choice questions produce a keyed answers envelope. The shared integration reports transport, status, malformed-envelope and gate failures rather than silently inventing decisions. Caller cancellation remains distinguishable from service failure.

**Acceptance criteria / DoD**

- Calls from different consumers and DI scopes share the same gate, including retries.
- The whole successful response is timeout-bounded, not just header arrival.
- Retry eligibility remains transport errors, attempt timeouts, 502 and 503; never 504 or malformed envelopes.
- Tool filtering being disabled does not prevent an independently opted-in internal consumer from using the integration.
- The integration has no HTTP endpoint, persistence, decision cache or provider-routing role.

### 3. Preserve the first consumer's contract

[HLD 014](../014-decisions-tools-filter/README.md) owns tool selection and remains authoritative for its default-off flag, every operator setting it documents, 20-question chunks, prompt/instruction caps, history protection and conservative fail-open behavior. Separating transport ownership must not silently change those contracts or inference forwarding.

**Acceptance criteria / DoD**

- Every tool-filter operator setting HLD 014 documents — including `MinKeepTools`, `AlwaysKeep` and `StickyPerSession`, added after this design was drafted — retains its documented default, effective value and conventional precedence.
- Disabled and no-drop requests retain exact body identity; successful pruning and failure handling remain equivalent.
- No new shipped configuration enables tool filtering or any additional consumer.
- Sharing the integration does not broaden which inference bodies may be inspected.

## Core Separation of Concerns

> Consumers own the question and the consequence; the shared integration owns the bounded conversation with the decision service.

Application owns the consumer-neutral contract and each feature's interpretation. Infrastructure owns HTTP execution and shared resource control. Host composes them without becoming a generic decision engine.

## Guiding Principle

Reuse the existing service boundary, not the first consumer's policy. Add no registry, workflow DSL, provider plugin system or decision-tree engine without a concrete consumer requirement.

## Non-Goals

Public decision APIs, ai-review integration, cross-repository packages, new production consumers, multiple classifier destinations, caching, durable results and changes to inference routing.

## Runtime Boundary

The goals and ownership thesis were approved on 2026-10-22 and the extraction was implemented on 2026-10-24. Application owns a consumer-neutral transport profile (destination, path, default model, maximum attempts, timeout) and a consumer opt-in contract; the integration is required — and its settings validated, its resilience profile built from them — while any registered consumer is opted in. Tool filtering is the only production consumer and opts in through its existing enable flag. Infrastructure owns the HTTP client and a process-wide admission gate registered as its own singleton component. HLD 014's accepted records remain authoritative for tool-selection policy. This HLD neither supersedes them nor authorizes additional prompt inspection.

Existing operator names remain compatible; the legacy tool prefix does not imply that the tool filter controls all consumers. No additional configuration surface is required for the first-consumer extraction ([LADR-02](ladrs/LADR-02-compatible-service-configuration.md)).

## Diagrams

- [System context](diagrams/c4-context.md) — external dependencies; no decision endpoint.
- [Decision ownership](diagrams/decision-ownership.md) — deterministic policy versus model evidence.
- [Bounded call](diagrams/sequence-bounded-call.md) — gate, complete response, failure and cancellation.

## Architecture Decisions

| LADR | Decision | Status |
|---|---|---|
| [01](ladrs/LADR-01-consumer-owned-policy.md) | Internal evidence contract; consumer-owned policy | Accepted |
| [02](ladrs/LADR-02-compatible-service-configuration.md) | Shared settings ownership with existing operator compatibility | Accepted |
| [03](ladrs/LADR-03-shared-uncached-resource-budget.md) | One uncached, process-wide service budget | Accepted |
| [04](ladrs/LADR-04-explicit-failure-semantics.md) | Bounded transport; explicit failures and caller cancellation | Accepted |

## Non-Functional Requirements

| NFR | Attribute | Target | Status |
|---|---|---|---|
| [01](nfrs/NFR-01-bounded-shared-execution.md) | Resource bound | At most one in-flight call; bounded wait and attempts | Accepted |
| [02](nfrs/NFR-02-first-consumer-compatibility.md) | Compatibility | No HLD 014 configuration or behavioral regression | Accepted |
| [03](nfrs/NFR-03-consumer-call-isolation.md) | Isolation | No cross-call state, answer or cancellation leakage | Accepted |

NativeAOT remains the repository deployment baseline. The extraction's macOS ARM64 publish and native smoke are recorded in the changelog; Linux AMD64/ARM64 native-runner gates and real cache-hit measurement remain open under HLD 014.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-22 | Goals approved; shared-boundary LADRs, NFRs and diagrams drafted; runtime unchanged | #138 |
| 2026-10-24 | Settings count retired for a count-free compatibility condition; transport deviations recorded in LADR-02; gate ownership stated in LADR-03; runtime unchanged | #156 |
| 2026-10-24 | Extraction implemented; HLD, LADR-01..04 and NFR-01..03 Accepted. Consumer-neutral transport profile mapped from the unchanged operator surface; any-consumer opt-in predicate drives validation and the resilience profile; dedicated singleton gate; default model supplied when a consumer omits one. macOS ARM64 NativeAOT verified; Linux AMD64/ARM64 native runners remain open | #156 |
