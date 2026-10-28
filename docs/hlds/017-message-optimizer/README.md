# Decision-Gated Message Optimizer — High-Level Design

| | |
|---|---|
| **Status** | Accepted |
| **Initiative** | LLM-Optimization |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | [GitHub #164](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/164) |
| **Depends on** | HLD 015 amendment (see [Dependencies](#dependencies)) |
| **Last updated** | 2026-10-25 |

## Intent

Agent users type prose the end LLM does not need. An operator may opt into a proof-reader: the imposter asks the decision model whether each unhandled user message can be tightened and, if so, has an optimizer LLM rewrite it — filler gone, technical facts and payloads exact — before upstream. Cost: up to one decision call and one optimizer call (plus at most one retry) per unhandled message, in series, before first upstream byte; user text to two operator services; answer-quality risk measured first ([Open item 1](#open-items)).

This design narrows the routing transparent-proxy rule: the optimizer becomes an eighth sanctioned request-rewrite class (after (1) managed auth header, (2) caching injection, (3) request normalization, (4) session-identity forwarding, (5) decision-tool filtering, (6) first-seen tool ordering and (7) opt-in Messages->Chat bridging (HLD 016)) and the first that rewrites `messages` content. The rule stays as written until this HLD is Accepted.

## Key Goals

### 1. Explicit opt-in with layered settings

`LLM_MESSAGES_ENABLED` (default `false`) switches the feature. The gate is a decision-service consumer whose `DECISIONS_MESSAGES_*` override shared `DECISIONS_*` defaults (which also default `DECISIONS_TOOLS_*`). The optimizer has its own complete `LLM_MESSAGES_*` set with no fallback. Settings are startup-scoped and validated only for parts effectively on ([LADR-06](ladrs/LADR-06-layered-settings.md)).

**Acceptance criteria / DoD**

- No shipped appsettings or container command enables the feature; while it is off, bodies are forwarded without parsing or model calls.
- Every variable resolves section value → `DECISIONS_*` → built-in default; existing `DECISIONS_TOOLS_*` configurations keep their effective values.
- No `DECISIONS_ENABLED` exists; `MIN_KEEP`, `ALWAYS_KEEP` and `STICKY_PER_SESSION` affect only the tool filter.
- The four switch combinations in LADR-06 behave as tabled.
- Precedence and switch resolution hold on the NativeAOT executable, not only under JIT.

### 2. Decide before rewriting

Human segments shorter than optional `LLM_MESSAGES_MIN_CHARS` (default set from measurement) skip both models. Otherwise the decision model is asked; only a positive answer reaches the optimizer. Runs at the forward seam, parallel to the tool filter ([LADR-02](ladrs/LADR-02-pipeline-position.md), [LADR-08](ladrs/LADR-08-parallel-strategies.md)).

**Acceptance criteria / DoD**

- A message below the minimum makes no gate or optimizer call.
- A negative answer forwards the message unchanged; a positive one sends it to the optimizer.
- Requests carrying an imposter switch (HLD 010) behave exactly as today, and switch text never reaches either model.
- The tool filter classifies the original text, and the forwarded body carries both branches' edits.

### 3. Rewrite only what the user wrote

Every human-authored message in the array not yet remembered is a candidate, handled one at a time ([LADR-03](ladrs/LADR-03-human-spans-only.md)). Harness spans, tool results, assistant turns, system prompts and non-text content pass through; reassembled around them. The optimizer removes filler, pleasantries, hedging and redundant phrasing and keeps every protected element: code, links, commands, mentions, tool and skill names, numbers, negations and structure. The proxy checks every rewrite ([LADR-04](ladrs/LADR-04-proxy-validation.md)).

**Acceptance criteria / DoD**

- Everything outside the candidate's human spans is byte-identical after optimization, including every other top-level value.
- A rewrite that alters or drops a checked protected element (LADR-04), adds an identifier, path or number, or is not shorter is discarded; after at most one targeted retry the original is forwarded.

### 4. Rewrite once, replay forever

The proxy caches, per session, the text it forwarded for each original human message — optimized or original — keyed by a checksum of the original text and optimizer version, and replaces every matching message with it, without model calls. The cache is in memory with a five-day sliding TTL ([LADR-05](ladrs/LADR-05-proxy-side-memory.md)). No message compressed twice; cache prefix stable ([NFR-01](nfrs/NFR-01-cache-prefix-stability.md)).

**Acceptance criteria / DoD**

- A remembered message makes no model call and is forwarded with the same bytes as before.
- A new optimizer version does not reuse old entries.
- No marker is added to forwarded text, and nothing is written to disk.
- A request without a captured session id is not cached; its messages are optimized on every request.
- Applies to Anthropic Messages and Chat Completions; Responses is [Open item 4](#open-items).

### 5. Never fail the inference request

Both models optional, each behind its own circuit: one failed call (after retries) opens it for an optional cooldown (default 180 s). Failed/open gate continues to optimizer; failed/open optimizer forwards original. Opens log at Error ([LADR-07](ladrs/LADR-07-fail-open-circuits.md)).

**Acceptance criteria / DoD**

- No failure in this design changes the caller-visible status or body shape.
- Within a cooldown the open section makes zero calls ([NFR-03](nfrs/NFR-03-failure-cost.md)).
- Caller cancellation propagates, makes no upstream call and opens no circuit.
- A failing or timed-out branch leaves the other branch's edit applied; overlapping edits forward the original; caller cancellation stops both.

## Core Separation of Concerns

> The models propose; the proxy decides what is forwarded.

The gate answers "worth rewriting?" and the optimizer proposes text; the proxy owns span selection, validation, memory, circuits and the final body. Each optimization is a strategy returning an edit; a coordinator runs the enabled ones concurrently and merges them. Application owns those rules, Infrastructure the two transports, Host invokes the coordinator.

## Guiding Principle — Uncertainty forwards the original

> When in doubt, send what the user wrote.

## Build baseline
- NativeAOT baseline: NFRs verified on the published executable for both arches.

## Non-Goals

No tool-output compression/masking (tracked with tool-decision work); no history summarization/compaction (harness owns `/compact`); no rewriting assistant turns, system prompts or harness context; no output-style rules into the end LLM; no routing simple asks to a cheaper model; no cross-dialect translation.

## Dependencies

HLD 015 supplies one transport, resilience profile and process-wide gate. This design needs a resolved profile per section, a circuit per section (HLD 014 LADR-11 pattern) and one admission gate per distinct endpoint+model, amending HLD 015 LADR-03's single process-wide gate — see HLD 015 [LADR-05](../015-shared-decision-model-integration/ladrs/LADR-05-per-section-profiles-circuits-gates.md) (Accepted). HLD 014 LADR-05 and LADR-07 must also be amended: the tool filter returns an edit instead of a whole body (LADR-08), and the shared session-scoped memory (HLD 014 LADR-09) gains a per-instance lifetime and an uncapped mode.

The HLD 015 amendment is Accepted as LADR-05; the HLD 014 edit-return amendment is Accepted as [LADR-12](../014-decisions-tools-filter/ladrs/LADR-12-tool-filter-returns-edit.md).

## Open items

| # | Question | Recommendation (*Recorded* in #164; *Proposed* here, not yet agreed) |
|---|---|---|
| 1 | How is answer quality proven? CAVEWOMAN (arXiv 2606.24083) found caveman-styled prompts gave worse answers. | *Recorded:* A/B on tokens per task; probe recall, artifact trail, continuation, decisions; similarity scores are not evidence. Exit gate. |
| 2 | Which turns may be rewritten? | *Resolved (2026-10-25, KISS):* every human message not yet remembered; remembered ones are replayed (LADR-03, LADR-05). |
| 3 | Restart, enablement or a scope idle past its TTL: uncached history is rewritten, missing the cache once. | *Proposed:* accept one cache miss per restart, enablement or expiry unless measurement shows otherwise. |
| 4 | Responses API in scope? | *Proposed:* out of v1; revisit with a Codex measurement. |
| 5 | Does the tool filter classify the original or the optimized text? | *Resolved:* original text — the branches run in parallel (LADR-08). |
| 6 | Route scope? | *Resolved (2026-10-27, #207):* per-provider `OptimizeMessages` (`<PROVIDER>_OPTIMIZE_MESSAGES`); only `false` opts a route out, unset/`true` keep it on for passthrough and imposter routes alike. Nothing opts a route out automatically; opting out is the operator's call for routes where latency outweighs the token saving or the upstream still bills per request (GitHub Copilot bills tokens through AI credits since 2026-06-01, except legacy annual plans). The tool filter is unaffected. |
| 7 | Latency ceiling for gate + rewrite? | *Proposed:* measure. First request after restart/enablement pays a gate call and a rewrite per unremembered message, in series; a down or bypassed gate sends each to the optimizer. Measure "time-to-first-byte is the slower branch" (LADR-02, LADR-08) with delayed stubs. |
| 8 | Memory scope without a captured session id (HLD 009)? | *Resolved (2026-10-25, revised):* not cached — optimized on every request (LADR-05). |

## Diagrams

- [System context](diagrams/c4-context.md)
- [Per-message flow](diagrams/sequence-optimize.md)

## Architecture Decisions

| LADR | Decision | Status |
|---|---|---|
| [01](ladrs/LADR-01-two-model-pipeline.md) | Classifier gate and generation optimizer on separate transports | Draft |
| [02](ladrs/LADR-02-pipeline-position.md) | At the forward seam, after switches and routing transforms | Draft |
| [03](ladrs/LADR-03-human-spans-only.md) | Rewrite human spans of each unremembered human message, in series; reassemble around the rest | Draft |
| [04](ladrs/LADR-04-proxy-validation.md) | Proxy validates every rewrite; the original wins on doubt | Draft |
| [05](ladrs/LADR-05-proxy-side-memory.md) | Session-scoped checksum cache of forwarded messages, in memory, sliding five-day TTL | Draft |
| [06](ladrs/LADR-06-layered-settings.md) | Layered decision settings and switch resolution | Draft |
| [07](ladrs/LADR-07-fail-open-circuits.md) | Per-section circuits; gate failure continues, optimizer failure forwards the original | Draft |
| [08](ladrs/LADR-08-parallel-strategies.md) | Optimizations as strategies run concurrently; edits merged last | Draft |

## Non-Functional Requirements

| NFR | Attribute | Target (summary) | Status |
|---|---|---|---|
| [01](nfrs/NFR-01-cache-prefix-stability.md) | Cache-prefix stability | Cached messages byte-identical on later requests | Draft |
| [02](nfrs/NFR-02-payload-fidelity.md) | Payload fidelity | Zero altered protected elements across the fixture corpus | Draft |
| [03](nfrs/NFR-03-failure-cost.md) | Failure cost | Open circuit: zero calls; at most one failed call per section per cooldown | Draft |
| [04](nfrs/NFR-04-message-egress.md) | Message egress | One message per optimizer call, nothing else; no message content in logs | Draft |

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-25 | Authored in Discovery from #164: LADR-01..07, NFR-01..04, Open items 1..8 | #164 |
| 2026-10-25 | Amended: LADR-08 parallel strategies; per-message candidates (Q21); isolated optimizer requests; switch exclusion | #164 |
| 2026-10-25 | Review fixes: Core Separation; HLD 014 dependency; Open item 8 resolved | #164 |
| 2026-10-25 | LADR-05: session-scoped checksum cache, in memory, sliding TTL ≤ 5 days, uncapped; sessionless not cached | #164 |
| 2026-10-25 | Dependencies reference HLD 015 LADR-05 Draft amendment for per-section profiles, circuits, gates | #164 |
| 2026-10-25 | Dependencies reference HLD 014 LADR-12 Draft amendment for edit-return contract | #164 |
| 2026-10-25 | Accepted by owner decision: HLD 015 LADR-05 and HLD 014 LADR-12 Accepted; Phase B implementation ungated | #164 |
| 2026-10-27 | Open item 6 resolved: per-provider `OptimizeMessages` opt-out implemented | #207 |
| 2026-10-27 | NFR-01 verified by an L2 replay (WT-14); Linux native runners still open | #209 |
| 2026-10-27 | NFR-02 verified by per-class fixtures and a deterministic corpus (WT-15); hyphenated dollar commands and repeated negations now protected | #212 |
| 2026-10-27 | NFR-03 failure cost proven by an L2 file over the real clients and pipelines, plus a host-platform native smoke; no production change | #214 |
| 2026-10-27 | NFR-04 message egress proven by an L2 request/log capture and a native smoke; two wording gaps recorded (Debug inbound dump, span hash not logged); no production change | #216 |
| 2026-10-27 | NFR-04 wording amended to exclude the opt-in Debug request dump; span hash on the Debug outcome line tracked as follow-up WT-20 | #216 |
