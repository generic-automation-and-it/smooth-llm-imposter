# CodexToOpenAiSdk Transformer — High-Level Design

| | |
|---|---|
| **Status** | Completed |
| **Owner** | Routing feature (@generik0) |
| **Tracker** | [Issue #19 — Codex tool-name 400 on opencode/Moonshot](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/19) |
| **Last updated** | 2026-06-21 |

> Discovery / prototyping HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue/work tracker (`.context/work-tasks/codex-to-openai-sdk-transformer.md`).

## Intent

Agent clients (e.g. Codex) emit OpenAI-dialect requests shaped for OpenAI's own backend.
Strict OpenAI-*compatible* upstreams — the kind a matched imposter route forwards to — apply
tighter validation and reject some of those shapes outright, before doing any work. Today the
router is a faithful transparent proxy, so the client must be reconfigured per-upstream to stay
compatible. This HLD introduces an **opt-in request-normalization seam** on matched OpenAI
imposter routes: a place where the router reshapes the inbound request into a form a strict
upstream accepts, **editing only the request and never the response**, so clients stay vanilla.

> **LADR-05 update.** Tool normalization remains request-only. But the separately-existing
> `OpenAiUpstreamApi: chat_completions` *downgrade* (Responses request → Chat request) leaves the
> client an unparseable Chat response, so LADR-05 makes that downgrade **bidirectional**: the Chat
> response stream is translated back to Responses events (incrementally, never buffered). That is the
> one sanctioned response edit; all other routes stay byte-for-byte.

This HLD designs **the seam and the policy that governs it**. The specific normalizations (which
fields are reshaped and how) are deliberately **out of scope** here and are introduced as
separate, individually-scoped increments tracked in the worktask.

## Key Goals

### 1. A request-normalization seam on the OpenAI imposter path

Introduce a normalization stage that runs only on the **OpenAI dialect** and only on a **matched
imposter route**, composing with the existing model/caching/Responses→Chat transform. It is a
pure body transform (string-in/string-out) in the Application layer; it never sees `HttpContext`
and never touches the transport. Passthrough and default routes remain byte-transparent.

**Acceptance criteria / DoD**

- The seam exists as a distinct, named normalization stage invoked by the OpenAI transform path.
- It is reachable **only** on OpenAI-dialect, matched-imposter requests; passthrough/default and
  Anthropic routes are provably unaffected.
- Adding a future normalization is a localized change to the seam, not a new branch in the router.

### 2. Request-only — the response is never rewritten

Normalization edits the outbound request body and nothing else. The streamed upstream response is
relayed byte-for-byte, preserving the streaming non-negotiable (HLD 001 LADR-003). Where a naive
normalization would force a value to be mapped *back* through the response, the design instead
**removes** the offending request element rather than remapping it — keeping the transform
one-directional.

**Acceptance criteria / DoD**

- No code path reads or rewrites the upstream response as part of normalization.
- The design admits no per-request state that must survive into the response stream.

### 3. Per-provider opt-in, safe by default

Normalization is selected by provider configuration and is **off by default**. A provider that
does not opt in behaves exactly as today (byte-identical forwarding). Configuration is validated
at startup alongside the other provider options.

**Acceptance criteria / DoD**

- With normalization unset/off, the forwarded request is byte-identical to current behavior.
- The opt-in is per-provider config, surfaced in startup validation.

### 4. Composable and extensible

The seam composes with the existing `OpenAiRequestTransformer` (model rewrite, caching,
Responses→Chat) and is structured so independent normalizations can be added without
re-litigating the seam or its placement.

**Acceptance criteria / DoD**

- The seam's placement relative to the existing transform steps is specified and order-stable.
- A new normalization can be added behind the same opt-in without changing the router or forwarder.

## Core Separation of Concerns

> The client stays vanilla; the proxy owns upstream compatibility — and it earns that ownership
> by editing the **request only**, never the response.

Compatibility shimming is an upstream concern, not a client concern. Centralizing it in the router
keeps every client unmodified, but it must not cost the router its transparent-streaming property.
The resolution is a hard directional boundary: normalization is allowed to reshape what goes *to*
the upstream, and is forbidden from touching what comes *back*. Any normalization that cannot live
within that boundary is redesigned (e.g. by removing an element rather than remapping it) or
rejected.

## Guiding Principle — Normalize in, never out

> Reshape the request on the way in; relay the response untouched.

- The router owns upstream compatibility so clients can stay vanilla.
- We will **not** rewrite, buffer, or reinterpret the response stream — and we will not introduce
  request-side state that a response rewrite would later need.

> **Amended by LADR-05.** One sanctioned exception: when the router *downgrades* a `/responses`
> request to Chat Completions, the response wire-shape no longer matches what the client can parse, so
> it must be translated back to Responses events. The principle is narrowed to **never *buffer* or
> *replay* the response, and never touch it on transparent/passthrough routes** — an incremental,
> forward-only translation on the explicitly-downgraded path is permitted (LADR-05 / NFR-05).

---

## Diagrams

- [System Context (C1) + request flow](./diagrams/c4-context.md)

## Evidence & Evals

The strict upstream's tool-validation behavior is characterized empirically — reproducible request
bodies + observed results — in [`./examples/upstream-tool-validation.md`](./examples/upstream-tool-validation.md).
The future normalizer's request-edit rules are derived from that contract, not assumed.

## Architecture Decisions (LADRs)

LADRs 01–N are strategic (*what* and *why*); later LADRs are tactical (*how*). Each is a
single decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-normalize-proxy-side-client-vanilla.md) | Normalize on the proxy side so the client stays vanilla; adds a third sanctioned request-rewrite class. **Supersedes HLD 001 LADR-006.** | Accepted |
| [LADR-02](./ladrs/LADR-02-request-only-prefer-removal-over-remap.md) | Request-only normalization; prefer removing an offending element over remapping it through the response. | Accepted |
| [LADR-03](./ladrs/LADR-03-per-provider-opt-in-safe-default.md) | Per-provider opt-in, off by default, startup-validated. | Accepted |
| [LADR-04](./ladrs/LADR-04-live-upstream-evals-separate-gate.md) | Live upstream evals run as a separate, secret-gated PR gate (`pr_evals_gate`), classified **L3**; hermetic L0/L2 tests stay external-free. | Accepted |
| [LADR-05](./ladrs/LADR-05-bidirectional-responses-chat-bridge.md) | The `/responses`→Chat downgrade is **bidirectional**: translate the Chat response stream back to Responses events (incremental, never buffered). **Amends LADR-02; narrows HLD 001 LADR-003.** | Accepted |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-request-only-no-response-rewrite.md) | Streaming integrity | Response bytes relayed unchanged on all paths **except** the LADR-05 downgrade bridge; zero response-path reads in tool normalization | Accepted (scoped by NFR-05) |
| [NFR-02](./nfrs/NFR-02-idempotency-safe-default.md) | Correctness / safety | Disabled ⇒ byte-identical; enabled normalization is idempotent | Accepted |
| [NFR-03](./nfrs/NFR-03-performance-single-pass.md) | Performance | No extra full-body JSON pass beyond the existing transform | Accepted |
| [NFR-04](./nfrs/NFR-04-upstream-contract-conformance.md) | Conformance | Normalized request accepted (200) by live upstream; contract rules hold; verified by `pr_evals_gate` | Accepted |
| [NFR-05](./nfrs/NFR-05-streaming-response-translation.md) | Streaming response translation | Chat→Responses stream translated **incrementally** (never buffered), bounded forward-only state, terminates in one `response.completed` | Accepted |
