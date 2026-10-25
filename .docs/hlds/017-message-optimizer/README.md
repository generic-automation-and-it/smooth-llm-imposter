# Decision-Gated Message Optimizer — High-Level Design

| | |
|---|---|
| **Status** | In Discovery |
| **Initiative** | LLM-Optimization |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | [GitHub #164](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/164) |
| **Depends on** | HLD 015 amendment (see [Dependencies](#dependencies)) |
| **Last updated** | 2026-10-25 |

> Discovery HLD: intent and spec only. Sequencing and sub-issues live in the tracker.

## Intent

Agent users type prose the end LLM does not need: greetings, hedging, restatement. An operator may opt into a proof-reader: the imposter asks the decision model whether each user message it has not handled before can be tightened and, if so, has an optimizer LLM rewrite it — filler gone, every technical fact and payload exact — before the request continues upstream. The default path is unchanged. The cost is up to two model calls before the first upstream byte, user text sent to two operator-configured services, and an answer-quality risk that must be measured before the feature is used ([Open item 1](#open-items)).

This design narrows the routing transparent-proxy rule: the optimizer becomes a seventh sanctioned request-rewrite class and the first that rewrites `messages` content. The rule stays as written until this HLD is Accepted.

## Key Goals

### 1. Explicit opt-in with layered settings

`LLM_MESSAGES_ENABLED` (default `false`) switches the feature. The gate is a decision-service consumer whose `DECISIONS_MESSAGES_*` settings override shared `DECISIONS_*` defaults, which also default the tool filter's `DECISIONS_TOOLS_*`. The optimizer has its own complete `LLM_MESSAGES_*` set with no fallback. Settings are startup-scoped and validated only for the parts that are effectively on ([LADR-06](ladrs/LADR-06-layered-settings.md)).

**Acceptance criteria / DoD**

- No shipped appsettings or container command enables the feature; while it is off, bodies are forwarded without parsing or model calls.
- Every variable resolves section value → `DECISIONS_*` → built-in default; existing `DECISIONS_TOOLS_*` configurations keep their effective values.
- No `DECISIONS_ENABLED` exists; `MIN_KEEP`, `ALWAYS_KEEP` and `STICKY_PER_SESSION` affect only the tool filter.
- The four switch combinations in LADR-06 behave as tabled.
- Precedence and switch resolution hold on the NativeAOT executable, not only under JIT.

### 2. Decide before rewriting

A message shorter than an optional `LLM_MESSAGES_MIN_CHARS` (default set from measurement) skips the gate and the optimizer. Otherwise the decision model is asked whether the message can be optimized, and only a positive answer reaches the optimizer. The stage runs at the forward seam, in parallel with the tool filter ([LADR-02](ladrs/LADR-02-pipeline-position.md), [LADR-08](ladrs/LADR-08-parallel-strategies.md)).

**Acceptance criteria / DoD**

- A message below the minimum makes no gate or optimizer call.
- A negative answer forwards the message unchanged; a positive one sends it to the optimizer.
- Requests carrying an imposter switch (HLD 010) behave exactly as today, and switch text never reaches either model.
- The tool filter classifies the original text, and the forwarded body carries both branches' edits.

### 3. Rewrite only what the user wrote

Every human-authored message in the array that is not yet remembered is a candidate, handled one at a time ([LADR-03](ladrs/LADR-03-human-spans-only.md)). Harness spans (`<system-reminder>`, `<system_instruction>`, local-command echo, skill command blocks), tool results, assistant turns, system prompts and non-text content pass through; the message is reassembled around them. The optimizer removes filler, pleasantries, hedging and redundant phrasing and keeps code, inline code, URLs and links, paths, commands, slash and dollar commands, `@` mentions, tool, skill, hook, plugin and rule names, technical terms, proper nouns, numbers, versions, environment variables, negations and markdown structure. The proxy checks every rewrite against its original ([LADR-04](ladrs/LADR-04-proxy-validation.md)).

**Acceptance criteria / DoD**

- Everything outside the candidate's human spans is byte-identical after optimization, including every other top-level value.
- A rewrite that alters or drops a protected element, adds an identifier, path or number, or is not shorter is discarded; after at most one targeted retry the original is forwarded.

### 4. Rewrite once, replay forever

Clients resend their own original history every turn, so a marker written upstream never comes back. The proxy remembers what it forwarded for each human turn — optimized text or the original — keyed by a hash of the original text and the optimizer version, and replays it on later turns without calling either model ([LADR-05](ladrs/LADR-05-proxy-side-memory.md)). No message is compressed twice, and the cache prefix stays stable ([NFR-01](nfrs/NFR-01-cache-prefix-stability.md)).

**Acceptance criteria / DoD**

- A remembered turn makes no model call and is forwarded with the same bytes as before.
- A new optimizer version does not reuse old entries.
- No marker is added to forwarded text.
- Applies to Anthropic Messages and Chat Completions; Responses is [Open item 4](#open-items).

### 5. Never fail the inference request

Both models are optional. Each sits behind its own circuit: one failed call, after retries, opens it for an optional cooldown (default 180 s) during which no call is made. A failed or open gate continues to the optimizer; a failed or open optimizer forwards the original. Opening logs at Error ([LADR-07](ladrs/LADR-07-fail-open-circuits.md)).

**Acceptance criteria / DoD**

- No failure in this design changes the caller-visible status or body shape.
- Within a cooldown the open section makes zero calls ([NFR-03](nfrs/NFR-03-failure-cost.md)).
- Caller cancellation propagates, makes no upstream call and opens no circuit.

## Core Separation of Concerns

> The models propose; the proxy decides what is forwarded.

The gate answers "worth rewriting?" and the optimizer proposes text. The proxy owns span selection, validation, memory, circuits and the final body. Each optimization is a strategy that returns an edit; a coordinator runs the enabled strategies concurrently and merges their edits. Application owns those rules; Infrastructure owns the two transports; Host invokes the coordinator.

## Guiding Principle — Uncertainty forwards the original

> When in doubt, send what the user wrote.

- NativeAOT is the repository baseline: every NFR is verified on the published native executable for `linux/amd64` and `linux/arm64`, not only under JIT.

## Non-Goals

Compressing or masking tool output (tracked with the tool-decision work); summarizing or compacting history (the harness owns `/compact`); rewriting assistant turns, system prompts or harness context; injecting output-style rules into the end LLM; routing simple asks to a cheaper model; cross-dialect translation.

## Dependencies

HLD 015 defines one transport profile, one resilience profile and one process-wide gate. This design needs a resolved profile per decision section, a circuit per section (as HLD 014 LADR-11 does for tools) and one admission gate per distinct endpoint + model, so sections sharing a model take turns and sections on different endpoints run in parallel. HLD 015 is amended in its own docs change before implementation.

## Open items

| # | Question | Recommendation (*Recorded* = already in #164; *Proposed* = new here, not yet agreed) |
|---|---|---|
| 1 | How is answer quality proven? CAVEWOMAN (arXiv 2606.24083) found caveman-styled prompts gave worse answers. | *Recorded:* A/B the same tasks with and without the optimizer on tokens per task, then probe answers for recall, artifact trail, continuation and decisions across six dimensions; similarity scores are not evidence. Gate for leaving Discovery. |
| 2 | Which turns may be rewritten? | *Resolved (2026-10-25, KISS):* every human message not yet remembered; remembered ones are replayed (LADR-03, LADR-05). |
| 3 | Memory is lost on restart, and history from before the feature was enabled was never remembered; the next request rewrites that history, so the conversation misses the cache once. | *Proposed:* accept one cache miss per restart or enablement unless measurement shows otherwise. |
| 4 | Responses API in scope? | *Proposed:* out of v1; with `previous_response_id` only new turns reach the proxy, so revisit with a Codex measurement. |
| 5 | Does the tool filter classify the original or the optimized text? | *Resolved:* original text — the branches run in parallel (LADR-08). |
| 6 | Route scope? | *Recorded:* per-route opt-out, because request-billed upstreams (HLD 013 Copilot) gain nothing and pay the latency. *Proposed:* both route kinds otherwise. |
| 7 | Latency ceiling for gate + rewrite? | *Proposed:* measure first. Messages are handled in series, so the first request after a restart or enablement pays one gate and one rewrite per unremembered message; a down or bypassed gate sends every one to the optimizer. |
| 8 | Memory scope without a captured session id (HLD 009)? | TBD — owner: maintainers, before Prototype. |

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
| [05](ladrs/LADR-05-proxy-side-memory.md) | Proxy-side memory of what was forwarded, not an in-text marker | Draft |
| [06](ladrs/LADR-06-layered-settings.md) | Layered decision settings and switch resolution | Draft |
| [07](ladrs/LADR-07-fail-open-circuits.md) | Per-section circuits; gate failure continues, optimizer failure forwards the original | Draft |
| [08](ladrs/LADR-08-parallel-strategies.md) | Optimizations as strategies run concurrently; edits merged last | Draft |

## Non-Functional Requirements

| NFR | Attribute | Target (summary) | Status |
|---|---|---|---|
| [01](nfrs/NFR-01-cache-prefix-stability.md) | Cache-prefix stability | Previously forwarded turns byte-identical on every later request | Draft |
| [02](nfrs/NFR-02-payload-fidelity.md) | Payload fidelity | Zero altered protected elements across the fixture corpus | Draft |
| [03](nfrs/NFR-03-failure-cost.md) | Failure cost | Open circuit: zero calls; at most one failed call per section per cooldown | Draft |
| [04](nfrs/NFR-04-message-egress.md) | Message egress | One message per optimizer call, nothing else; no message content in logs | Draft |

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-25 | Authored in Discovery from #164: LADR-01..07, NFR-01..04, Open items 1..8 | #164 |
