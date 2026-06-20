# AGENTS.md - CodexToOpenAiSdk Transformer

AI Context: HLD for the CodexToOpenAiSdk request-normalization transformer. Updated: 2026-06-20

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

Designs an opt-in, request-only normalization seam on matched OpenAI imposter routes so vanilla
clients work against strict upstreams. Intent in README, decisions in `ladrs/`, quality spec in `nfrs/`.

## Non-Negotiables

- **Request-only.** Do not read, buffer, or rewrite the upstream response, and do not create
  request-side state intended for response lookup (LADR-02). Prefer **removing** an offending
  request element over remapping it through the response.
- **Scope: OpenAI dialect + matched imposter route, opt-in only.** Never normalize passthrough/
  default or Anthropic routes; off-by-default providers must stay byte-transparent (LADR-03).
- **This seam designs the framework, not the transforms.** The specific normalizations (which
  fields, which rule) are out of scope for this HLD — do not encode tool-name rules, client-specific
  tool lists, or `User-Agent`/`originator` handling here.
- **Supersession is real:** HLD 001 LADR-006 is superseded by LADR-01 — when touching routing, treat
  proxy-side normalization as sanctioned, but add it as a documented exception to the transparent-
  proxy contract in `ROUTING_AGENTS.md`, not silently.
- **Live upstream evals are L3** — run only in a separate, secret-gated `pr_evals_gate` (org
  `OPENCODE_API_KEY`), never in the hermetic L0/L2 `pr-gate`; the gate stays **neutral** when the
  secret is absent (fork PRs) and never logs the key (LADR-04). Don't add external network/secrets
  to L0/L2.
- LADRs are Prototype/Draft status — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-01 | Normalize proxy-side; client stays vanilla (supersedes HLD 001 LADR-006) | Establishes a third sanctioned request-rewrite class; build against it, update `ROUTING_AGENTS.md` |
| LADR-02 | Request-only; prefer removal over remap | Touching the response path breaks streaming (HLD 001 LADR-003) and is forbidden |
| LADR-03 | Per-provider opt-in, off by default, startup-validated | Normalizing a provider that didn't opt in silently corrupts its requests |
| LADR-04 | Live evals = L3, separate secret-gated `pr_evals_gate` | Putting external network/secrets in the hermetic L0/L2 gate breaks the infra-free test design |

## Key Behaviors

- Composes with `OpenAiRequestTransformer` (model rewrite, caching, Responses→Chat) within its
  single parse→edit→serialize pass — normalization mutates the same parsed node graph, not a second
  parse (NFR-03).
- "Removal" normalizations have a documented capability cost: the removed element is gone for that
  request. That cost is per-normalization and is recorded where each normalization is introduced.

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- Disabled ⇒ byte-identical forwarding; enabled ⇒ idempotent (NFR-02). Build the seam so re-running
  it is a no-op and the off-path shares no code that could alter the body.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-20 | HLD authored (seam + policy only; transforms deferred). Supersedes HLD 001 LADR-006. | #19 |
| 2026-06-20 | Added `examples/upstream-tool-validation.md` eval. Empirically: upstream requires `tools[].type` ∈ {function, plugin} and `function.name` non-empty + `[A-Za-z0-9_-]` (no dots); leading `_` is tolerated. Root cause is unsupported tool *types* + dotted/empty names — **not** leading underscores. | #19 |
| 2026-06-20 | Eval matrix completed (P4–P7): name rule refined to `^[A-Za-z_][A-Za-z0-9_-]*$` (leading digit rejected, P5); `plugin` type unusable as a minimal shape (P6, TBD); normalized toolset accepted (P7→200). Added LADR-04 (L3 `pr_evals_gate`) + NFR-04 (conformance). | #19 |
